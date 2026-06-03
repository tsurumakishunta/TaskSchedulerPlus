using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskSchedulerPlus.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// ワークフローとタスク定義を JSON ファイルとして入出力するためのサービスです。
// 実行履歴は環境ごとの運用結果なので、移行対象には含めません。
public sealed class WorkflowJsonTransferService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISetupConfigurationStore setupConfiguration,
    ILogger<WorkflowJsonTransferService> logger)
{
    private const int TransferFormatVersion = 1;
    private const string ApplicationName = "Task Scheduler Plus";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<WorkflowExportResult> ExportAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var workflows = await db.JobFlows
            .Include(workflow => workflow.Nodes)
                .ThenInclude(node => node.JobDefinition)
            .OrderBy(workflow => workflow.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var document = CreateTransferDocument(workflows, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var taskCount = document.Workflows.Sum(workflow => workflow.Tasks.Count);
        var fileName = $"workflow-tasks-{DateTimeOffset.Now:yyyyMMddHHmmss}.json";

        logger.LogInformation(
            "ワークフローJSONをエクスポートしました。Workflows={WorkflowCount}, Tasks={TaskCount}",
            document.Workflows.Count,
            taskCount);

        return new WorkflowExportResult(json, fileName, document.Workflows.Count, taskCount);
    }

    public async Task<IReadOnlyList<WorkflowExportListItem>> SearchWorkflowsAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.JobFlows.AsNoTracking();
        var trimmedKeyword = keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedKeyword))
        {
            query = query.Where(workflow =>
                workflow.Name.Contains(trimmedKeyword) ||
                workflow.Description != null && workflow.Description.Contains(trimmedKeyword));
        }

        return await query
            .OrderBy(workflow => workflow.Name)
            .Select(workflow => new WorkflowExportListItem(
                workflow.Id,
                workflow.Name,
                workflow.Description,
                workflow.IsEnabled,
                workflow.ScheduleEnabled,
                workflow.NextRunAt,
                workflow.Nodes.Count))
            .ToListAsync(cancellationToken);
    }

    public Task<WorkflowZipExportResult> ExportAllZipAsync(CancellationToken cancellationToken = default) =>
        ExportZipAsync(null, "all", cancellationToken);

    public Task<WorkflowZipExportResult> ExportSelectedZipAsync(
        IReadOnlyCollection<int> workflowIds,
        CancellationToken cancellationToken = default)
    {
        if (workflowIds.Count == 0)
        {
            throw new WorkflowJsonTransferException("エクスポートするワークフローを選択してください。");
        }

        return ExportZipAsync(workflowIds, "selected", cancellationToken);
    }

    public async Task<WorkflowImportResult> ImportAsync(
        string json,
        CancellationToken cancellationToken = default)
    {
        var document = Deserialize(json);
        ValidateDocument(document);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var workflowNames = new HashSet<string>(
            await db.JobFlows.Select(workflow => workflow.Name).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var taskIds = new HashSet<Guid>(
            await db.JobNodes.Select(node => node.Id).ToListAsync(cancellationToken));
        var importStamp = DateTimeOffset.Now;
        var renamedWorkflowCount = 0;
        var regeneratedTaskIdCount = 0;
        var importedTaskCount = 0;

        foreach (var workflowItem in document.Workflows)
        {
            var workflowName = RequiredText(workflowItem.Name, "ワークフロー名", 100);
            var uniqueWorkflowName = BuildUniqueWorkflowName(workflowName, workflowNames, importStamp);
            if (!workflowName.Equals(uniqueWorkflowName, StringComparison.Ordinal))
            {
                renamedWorkflowCount++;
            }

            var workflow = CreateWorkflow(workflowItem, uniqueWorkflowName);
            var usedTaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedTasks = workflowItem.Tasks
                .OrderBy(task => task.DisplayOrder)
                .ThenBy(task => task.Name)
                .ToList();

            for (var index = 0; index < orderedTasks.Count; index++)
            {
                var taskItem = orderedTasks[index];
                ValidateTask(workflowName, taskItem, usedTaskNames);
                var nodeId = ResolveTaskId(taskItem.TaskId, taskIds, ref regeneratedTaskIdCount);
                workflow.Nodes.Add(CreateTaskNode(taskItem, nodeId, index));
                importedTaskCount++;
            }

            workflow.NextRunAt = workflow.ScheduleEnabled
                ? ScheduleCalculator.CalculateNextRun(workflow, DateTimeOffset.UtcNow, ResolveAppTimeZone())
                : null;
            workflow.LastScheduledRunQueuedAt = null;
            db.JobFlows.Add(workflow);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "ワークフローJSONをインポートしました。Workflows={WorkflowCount}, Tasks={TaskCount}, RenamedWorkflows={RenamedWorkflowCount}, RegeneratedTaskIds={RegeneratedTaskIdCount}",
            document.Workflows.Count,
            importedTaskCount,
            renamedWorkflowCount,
            regeneratedTaskIdCount);

        return new WorkflowImportResult(
            document.Workflows.Count,
            importedTaskCount,
            renamedWorkflowCount,
            regeneratedTaskIdCount);
    }

    private async Task<WorkflowZipExportResult> ExportZipAsync(
        IReadOnlyCollection<int>? workflowIds,
        string fileScope,
        CancellationToken cancellationToken)
    {
        var targetWorkflowIds = workflowIds?
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (targetWorkflowIds is { Count: 0 })
        {
            throw new WorkflowJsonTransferException("エクスポートするワークフローを選択してください。");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.JobFlows
            .Include(workflow => workflow.Nodes)
                .ThenInclude(node => node.JobDefinition)
            .AsNoTracking();
        if (targetWorkflowIds is not null)
        {
            query = query.Where(workflow => targetWorkflowIds.Contains(workflow.Id));
        }

        var workflows = await query
            .OrderBy(workflow => workflow.Name)
            .ToListAsync(cancellationToken);
        if (workflows.Count == 0)
        {
            throw new WorkflowJsonTransferException("エクスポート対象のワークフローが見つかりません。");
        }

        if (targetWorkflowIds is not null && workflows.Count != targetWorkflowIds.Count)
        {
            throw new WorkflowJsonTransferException("選択されたワークフローの一部が見つかりません。再検索してから実行してください。");
        }

        var exportedAt = DateTimeOffset.UtcNow;
        var taskCount = workflows.Sum(workflow => workflow.Nodes.Count);
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            foreach (var workflow in workflows)
            {
                var document = CreateTransferDocument([workflow], exportedAt);
                var entry = archive.CreateEntry(BuildWorkflowEntryName(workflow), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await JsonSerializer.SerializeAsync(entryStream, document, JsonOptions, cancellationToken);
            }
        }

        var fileName = $"workflow-json-{fileScope}-{DateTimeOffset.Now:yyyyMMddHHmmss}.zip";
        logger.LogInformation(
            "ワークフローJSON ZIPをエクスポートしました。Workflows={WorkflowCount}, Tasks={TaskCount}, Scope={Scope}",
            workflows.Count,
            taskCount,
            fileScope);

        return new WorkflowZipExportResult(memoryStream.ToArray(), fileName, workflows.Count, taskCount);
    }

    private static WorkflowTransferDocument CreateTransferDocument(
        IEnumerable<JobFlow> workflows,
        DateTimeOffset exportedAt) => new()
    {
        ExportedAt = exportedAt,
        Workflows = workflows.Select(ToTransferItem).ToList()
    };

    private static WorkflowTransferItem ToTransferItem(JobFlow workflow) => new()
    {
        Name = workflow.Name,
        Description = workflow.Description,
        IsEnabled = workflow.IsEnabled,
        ScheduleEnabled = workflow.ScheduleEnabled,
        ScheduleType = workflow.ScheduleType,
        ScheduledStartAt = workflow.ScheduledStartAt,
        ScheduleIntervalMinutes = workflow.ScheduleIntervalMinutes,
        ScheduleEveryDays = workflow.ScheduleEveryDays,
        ScheduleEveryWeeks = workflow.ScheduleEveryWeeks,
        ScheduleDayOfMonth = workflow.ScheduleDayOfMonth,
        ScheduleEveryMonths = workflow.ScheduleEveryMonths,
        ScheduleDaysOfWeek = workflow.ScheduleDaysOfWeek,
        ScheduleEndAt = workflow.ScheduleEndAt,
        RepeatEnabled = workflow.RepeatEnabled,
        RepeatIntervalMinutes = workflow.RepeatIntervalMinutes,
        RepeatDurationMinutes = workflow.RepeatDurationMinutes,
        Tasks = workflow.Nodes
            .OrderBy(node => node.DisplayOrder)
            .Select(ToTransferTask)
            .ToList()
    };

    private static TaskTransferItem ToTransferTask(JobNode node) => new()
    {
        TaskId = node.Id,
        Name = node.Name,
        Description = node.JobDefinition.Description,
        DisplayOrder = node.DisplayOrder,
        Command = node.JobDefinition.Command,
        Arguments = node.JobDefinition.Arguments,
        WorkingDirectory = node.JobDefinition.WorkingDirectory,
        SuccessExitCode = node.JobDefinition.SuccessExitCode,
        TimeoutSeconds = node.JobDefinition.TimeoutSeconds,
        IsEnabled = node.JobDefinition.IsEnabled,
        RunNextOnSuccess = node.RunNextOnSuccess,
        StopOnFailure = node.StopOnFailure
    };

    private static WorkflowTransferDocument Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowTransferDocument>(json, JsonOptions)
                ?? throw new WorkflowJsonTransferException("JSONの内容を読み取れませんでした。");
        }
        catch (JsonException exception)
        {
            throw new WorkflowJsonTransferException("JSONの形式が正しくありません。", exception);
        }
    }

    private static void ValidateDocument(WorkflowTransferDocument document)
    {
        if (document.FormatVersion > TransferFormatVersion)
        {
            throw new WorkflowJsonTransferException(
                $"未対応のJSON形式です。対応形式: {TransferFormatVersion}, ファイル形式: {document.FormatVersion}");
        }

        if (!ApplicationName.Equals(document.Application, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowJsonTransferException("このシステム用のJSONファイルではありません。");
        }

        document.Workflows ??= [];
        if (document.Workflows.Count == 0)
        {
            throw new WorkflowJsonTransferException("インポート対象のワークフローがありません。");
        }

        foreach (var workflow in document.Workflows)
        {
            workflow.Tasks ??= [];
        }
    }

    private static JobFlow CreateWorkflow(WorkflowTransferItem item, string workflowName)
    {
        if (item.ScheduleEnabled && item.ScheduledStartAt is null)
        {
            throw new WorkflowJsonTransferException(
                $"ワークフロー「{workflowName}」の開始日時が設定されていません。");
        }

        if (item.ScheduleType == ScheduleType.IntervalMinutes &&
            item.ScheduleIntervalMinutes is null or < 1)
        {
            throw new WorkflowJsonTransferException(
                $"ワークフロー「{workflowName}」の実行間隔を1分以上で指定してください。");
        }

        if (item.ScheduleType == ScheduleType.Monthly &&
            item.ScheduleDayOfMonth is null or < 1 or > 31)
        {
            throw new WorkflowJsonTransferException(
                $"ワークフロー「{workflowName}」の月次実行日は1から31で指定してください。");
        }

        if (item.RepeatEnabled &&
            (item.RepeatIntervalMinutes is null or < 1 ||
             item.RepeatDurationMinutes is null or < 1 ||
             item.RepeatDurationMinutes < item.RepeatIntervalMinutes))
        {
            throw new WorkflowJsonTransferException(
                $"ワークフロー「{workflowName}」の繰り返し条件が正しくありません。");
        }

        return new JobFlow
        {
            Name = workflowName,
            Description = OptionalText(item.Description, 500),
            IsEnabled = item.IsEnabled,
            ScheduleEnabled = item.ScheduleEnabled,
            ScheduleType = item.ScheduleType,
            ScheduledStartAt = item.ScheduledStartAt,
            ScheduleIntervalMinutes = item.ScheduleType == ScheduleType.IntervalMinutes
                ? item.ScheduleIntervalMinutes
                : null,
            ScheduleEveryDays = Math.Max(1, item.ScheduleEveryDays),
            ScheduleEveryWeeks = Math.Max(1, item.ScheduleEveryWeeks),
            ScheduleDayOfMonth = item.ScheduleType == ScheduleType.Monthly
                ? item.ScheduleDayOfMonth
                : null,
            ScheduleEveryMonths = Math.Max(1, item.ScheduleEveryMonths),
            ScheduleDaysOfWeek = item.ScheduleType == ScheduleType.Weekly
                ? item.ScheduleDaysOfWeek
                : 0,
            ScheduleEndAt = item.ScheduleEndAt,
            RepeatEnabled = item.RepeatEnabled,
            RepeatIntervalMinutes = item.RepeatEnabled ? item.RepeatIntervalMinutes : null,
            RepeatDurationMinutes = item.RepeatEnabled ? item.RepeatDurationMinutes : null
        };
    }

    private static void ValidateTask(
        string workflowName,
        TaskTransferItem task,
        HashSet<string> usedTaskNames)
    {
        var taskName = RequiredText(task.Name, "タスク名", 100);
        if (!usedTaskNames.Add(taskName))
        {
            throw new WorkflowJsonTransferException(
                $"ワークフロー「{workflowName}」に同じタスク名「{taskName}」が含まれています。");
        }

        RequiredText(task.Command, $"タスク「{taskName}」の実行ファイル", 500);
        if (!HasExecutableExtension(task.Command))
        {
            throw new WorkflowJsonTransferException(
                $"タスク「{taskName}」の実行ファイルには .exe、.bat、.cmd のいずれかを指定してください。");
        }

        OptionalText(task.Description, 500);
        OptionalText(task.Arguments, 1000);
        OptionalText(task.WorkingDirectory, 500);

        if (task.TimeoutSeconds is < 1 or > 86400)
        {
            throw new WorkflowJsonTransferException(
                $"タスク「{taskName}」のタイムアウト秒数は1から86400で指定してください。");
        }
    }

    private static JobNode CreateTaskNode(TaskTransferItem task, Guid nodeId, int displayOrder)
    {
        var taskName = RequiredText(task.Name, "タスク名", 100);
        return new JobNode
        {
            Id = nodeId,
            Name = taskName,
            DisplayOrder = displayOrder,
            RunNextOnSuccess = task.RunNextOnSuccess,
            StopOnFailure = task.StopOnFailure,
            JobDefinition = new JobDefinition
            {
                Name = taskName,
                Description = OptionalText(task.Description, 500),
                Command = RequiredText(task.Command, $"タスク「{taskName}」の実行ファイル", 500),
                Arguments = OptionalText(task.Arguments, 1000),
                WorkingDirectory = OptionalText(task.WorkingDirectory, 500),
                SuccessExitCode = task.SuccessExitCode,
                TimeoutSeconds = task.TimeoutSeconds,
                IsEnabled = task.IsEnabled
            }
        };
    }

    private static Guid ResolveTaskId(
        Guid? importedTaskId,
        HashSet<Guid> existingTaskIds,
        ref int regeneratedTaskIdCount)
    {
        if (importedTaskId is Guid taskId &&
            taskId != Guid.Empty &&
            existingTaskIds.Add(taskId))
        {
            return taskId;
        }

        Guid newTaskId;
        do
        {
            newTaskId = Guid.NewGuid();
        }
        while (!existingTaskIds.Add(newTaskId));

        regeneratedTaskIdCount++;
        return newTaskId;
    }

    private static string BuildUniqueWorkflowName(
        string workflowName,
        HashSet<string> workflowNames,
        DateTimeOffset importStamp)
    {
        var candidate = TrimToMaxLength(workflowName, 100);
        if (workflowNames.Add(candidate))
        {
            return candidate;
        }

        for (var index = 1; index <= 999; index++)
        {
            var suffix = index == 1
                ? $" (取込{importStamp:yyyyMMddHHmmss})"
                : $" (取込{importStamp:yyyyMMddHHmmss}-{index})";
            candidate = TrimToMaxLength(workflowName, 100 - suffix.Length) + suffix;
            if (workflowNames.Add(candidate))
            {
                return candidate;
            }
        }

        throw new WorkflowJsonTransferException(
            $"ワークフロー名「{workflowName}」の一意な名前を作成できませんでした。");
    }

    private TimeZoneInfo ResolveAppTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                setupConfiguration.Current?.TimeZoneId ?? TimeZoneInfo.Local.Id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string RequiredText(string? value, string fieldName, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkflowJsonTransferException($"{fieldName}を入力してください。");
        }

        if (text.Length > maxLength)
        {
            throw new WorkflowJsonTransferException($"{fieldName}は{maxLength}文字以内で指定してください。");
        }

        return text;
    }

    private static string? OptionalText(string? value, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is not null && text.Length > maxLength)
        {
            throw new WorkflowJsonTransferException($"{maxLength}文字を超える文字列が含まれています。");
        }

        return text;
    }

    private static string TrimToMaxLength(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string BuildWorkflowEntryName(JobFlow workflow)
    {
        var safeWorkflowName = SanitizeFileName(workflow.Name);
        return $"{workflow.Id:D4}-{safeWorkflowName}.json";
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "workflow";
        }

        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private static bool HasExecutableExtension(string path)
    {
        var extension = Path.GetExtension(path.Trim().Trim('"'));
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class WorkflowTransferDocument
    {
        public int FormatVersion { get; set; } = TransferFormatVersion;

        public string Application { get; set; } = ApplicationName;

        public DateTimeOffset ExportedAt { get; set; }

        public List<WorkflowTransferItem> Workflows { get; set; } = [];
    }

    public sealed class WorkflowTransferItem
    {
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsEnabled { get; set; } = true;

        public bool ScheduleEnabled { get; set; }

        public ScheduleType ScheduleType { get; set; } = ScheduleType.Once;

        public DateTimeOffset? ScheduledStartAt { get; set; }

        public int? ScheduleIntervalMinutes { get; set; }

        public int ScheduleEveryDays { get; set; } = 1;

        public int ScheduleEveryWeeks { get; set; } = 1;

        public int? ScheduleDayOfMonth { get; set; }

        public int ScheduleEveryMonths { get; set; } = 1;

        public int ScheduleDaysOfWeek { get; set; }

        public DateTimeOffset? ScheduleEndAt { get; set; }

        public bool RepeatEnabled { get; set; }

        public int? RepeatIntervalMinutes { get; set; }

        public int? RepeatDurationMinutes { get; set; }

        public List<TaskTransferItem> Tasks { get; set; } = [];
    }

    public sealed class TaskTransferItem
    {
        public Guid? TaskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        public int DisplayOrder { get; set; }

        public string Command { get; set; } = string.Empty;

        public string? Arguments { get; set; }

        public string? WorkingDirectory { get; set; }

        public int SuccessExitCode { get; set; }

        public int TimeoutSeconds { get; set; } = 300;

        public bool IsEnabled { get; set; } = true;

        public bool RunNextOnSuccess { get; set; } = true;

        public bool StopOnFailure { get; set; } = true;
    }
}

public sealed record WorkflowExportResult(
    string Json,
    string FileName,
    int WorkflowCount,
    int TaskCount);

public sealed record WorkflowZipExportResult(
    byte[] ZipBytes,
    string FileName,
    int WorkflowCount,
    int TaskCount);

public sealed record WorkflowExportListItem(
    int Id,
    string Name,
    string? Description,
    bool IsEnabled,
    bool ScheduleEnabled,
    DateTimeOffset? NextRunAt,
    int TaskCount);

public sealed record WorkflowImportResult(
    int WorkflowCount,
    int TaskCount,
    int RenamedWorkflowCount,
    int RegeneratedTaskIdCount);

public sealed class WorkflowJsonTransferException : Exception
{
    public WorkflowJsonTransferException(string message)
        : base(message)
    {
    }

    public WorkflowJsonTransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
