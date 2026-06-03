using System.Diagnostics;
using TaskSchedulerPlus.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// キューに入ったワークフロー実行を順番に取り出し、外部プロセスとしてタスクを実行します。
public sealed class QueuedFlowRunDispatcher(
    ISetupConfigurationStore setupConfiguration,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ILogger<QueuedFlowRunDispatcher> logger) : BackgroundService
{
    private const int MaxStoredOutputLength = 20000;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "ワークフロー実行サービスを開始しました。PollingIntervalSeconds={PollingIntervalSeconds}",
            PollingInterval.TotalSeconds);

        try
        {
            using var timer = new PeriodicTimer(PollingInterval);
            while (!stoppingToken.IsCancellationRequested)
            {
                await ExecuteNextQueuedRunAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // アプリケーション停止時のキャンセルは通常終了として扱います。
        }
        finally
        {
            logger.LogInformation("ワークフロー実行サービスを停止しました。");
        }
    }

    private async Task ExecuteNextQueuedRunAsync(CancellationToken cancellationToken)
    {
        if (!setupConfiguration.IsConfigured)
        {
            // 初期設定前は DB 接続情報がないため、何もせず待機します。
            return;
        }

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            // 古い要求から順に1件だけ取得し、同時多重実行を避けます。
            var runId = await db.FlowRuns
                .Where(run => run.Status == ExecutionStatus.Queued)
                .OrderBy(run => run.RequestedAt)
                .Select(run => run.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (runId == 0)
            {
                return;
            }

            logger.LogInformation("待機中ワークフローを検出しました。RunId={RunId}", runId);
            await ExecuteFlowAsync(runId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "待機中ワークフローの確認に失敗しました。");
        }
    }

    private async Task ExecuteFlowAsync(int runId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.FlowRuns
            .Include(item => item.JobFlow)
                .ThenInclude(flow => flow.Nodes)
                    .ThenInclude(node => node.JobDefinition)
            .Include(item => item.JobRuns)
            .SingleAsync(item => item.Id == runId, cancellationToken);

        if (run.Status != ExecutionStatus.Queued)
        {
            // 取得後に別処理で状態が変わっている場合は二重実行しません。
            logger.LogInformation(
                "ワークフロー実行をスキップしました。RunId={RunId}, CurrentStatus={CurrentStatus}",
                runId,
                run.Status);
            return;
        }

        // 実行開始時点で Running に更新し、画面から現在状態を確認できるようにします。
        run.Status = ExecutionStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ワークフロー実行を開始しました。RunId={RunId}, FlowId={FlowId}, WorkflowName={WorkflowName}, TaskCount={TaskCount}",
            run.Id,
            run.JobFlowId,
            run.JobFlow.Name,
            run.JobFlow.Nodes.Count);

        try
        {
            if (run.JobFlow.Nodes.Count == 0)
            {
                // タスクが1件もないワークフローは実行できないため失敗扱いにします。
                logger.LogWarning(
                    "ワークフロー実行を失敗にしました。RunId={RunId}, FlowId={FlowId}, Reason={Reason}",
                    run.Id,
                    run.JobFlowId,
                    "タスク未登録");

                run.Status = ExecutionStatus.Failed;
                run.CompletedAt = DateTimeOffset.UtcNow;
                run.Message = "実行対象のタスクがありません。";
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var jobRuns = run.JobRuns.ToDictionary(item => item.JobNodeId);
            await ExecuteOrderedFlowAsync(run, jobRuns, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "ワークフロー実行を終了しました。RunId={RunId}, FlowId={FlowId}, Status={Status}, CompletedAt={CompletedAt}",
                run.Id,
                run.JobFlowId,
                run.Status,
                run.CompletedAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "ワークフロー実行 {RunId} が予期せず失敗しました。", runId);
            run.Status = ExecutionStatus.Failed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Message = Truncate("ワークフロー実行中に予期しないエラーが発生しました。詳細はサービスログを確認してください。");
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task ExecuteOrderedFlowAsync(
        FlowRun run,
        IReadOnlyDictionary<Guid, JobRun> jobRuns,
        CancellationToken cancellationToken)
    {
        var orderedNodes = run.JobFlow.Nodes.OrderBy(node => node.DisplayOrder).ToList();
        var hasFailure = false;

        // 現状は表示順で直列実行します。成功時/失敗時の設定により後続タスクの扱いを決めます。
        for (var index = 0; index < orderedNodes.Count; index++)
        {
            var node = orderedNodes[index];
            if (!node.JobDefinition.IsEnabled)
            {
                // 無効化されたタスクは実行せず、履歴上はスキップとして残します。
                logger.LogInformation(
                    "無効なタスクをスキップしました。RunId={RunId}, TaskId={TaskId}, TaskName={TaskName}",
                    run.Id,
                    node.Id,
                    node.Name);
                await MarkSkippedAsync(jobRuns[node.Id].Id, "タスク定義が無効です。", cancellationToken);
                continue;
            }

            var status = await RunJobAsync(jobRuns[node.Id].Id, node.JobDefinition, cancellationToken);
            if (status is ExecutionStatus.Failed or ExecutionStatus.TimedOut)
            {
                hasFailure = true;
                logger.LogWarning(
                    "タスク実行が失敗またはタイムアウトしました。RunId={RunId}, TaskId={TaskId}, TaskName={TaskName}, Status={Status}, StopOnFailure={StopOnFailure}",
                    run.Id,
                    node.Id,
                    node.Name,
                    status,
                    node.StopOnFailure);

                if (node.StopOnFailure)
                {
                    // 失敗時に停止する設定なら、残りのタスクは理由付きでスキップします。
                    logger.LogWarning(
                        "後続タスクをスキップします。RunId={RunId}, FailedTaskId={FailedTaskId}, RemainingTaskCount={RemainingTaskCount}",
                        run.Id,
                        node.Id,
                        orderedNodes.Count - index - 1);

                    await SkipRemainingAsync(
                        orderedNodes.Skip(index + 1),
                        jobRuns,
                        "前のタスクが異常終了し、設定により後続処理を実行しません。",
                        cancellationToken);
                    run.Message = "エラー時に後続処理を実行しない設定により、後続タスクを停止しました。";
                    break;
                }

                continue;
            }

            if (!node.RunNextOnSuccess)
            {
                // 正常終了しても後続へ進まない設定なら、残りのタスクはスキップします。
                logger.LogInformation(
                    "正常終了後の設定により後続タスクをスキップします。RunId={RunId}, TaskId={TaskId}, RemainingTaskCount={RemainingTaskCount}",
                    run.Id,
                    node.Id,
                    orderedNodes.Count - index - 1);

                await SkipRemainingAsync(
                    orderedNodes.Skip(index + 1),
                    jobRuns,
                    "前のタスクが正常終了しましたが、設定により後続処理を実行しません。",
                    cancellationToken);
                break;
            }
        }

        run.Status = hasFailure ? ExecutionStatus.Failed : ExecutionStatus.Succeeded;
        run.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task SkipRemainingAsync(
        IEnumerable<JobNode> nodes,
        IReadOnlyDictionary<Guid, JobRun> jobRuns,
        string reason,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            await MarkSkippedAsync(jobRuns[node.Id].Id, reason, cancellationToken);
        }
    }

    private async Task<ExecutionStatus> RunJobAsync(int jobRunId, JobDefinition job, CancellationToken stoppingToken)
    {
        // タスク単位の履歴を Running にしてから OS プロセスを起動します。
        await UpdateJobRunAsync(jobRunId, item =>
        {
            item.Status = ExecutionStatus.Running;
            item.StartedAt = DateTimeOffset.UtcNow;
        }, stoppingToken);

        var startInfo = CreateStartInfo(job);
        var stopwatch = Stopwatch.StartNew();
        var commandName = SafeCommandName(job.Command);

        logger.LogInformation(
            "タスク実行を開始しました。JobRunId={JobRunId}, JobDefinitionId={JobDefinitionId}, TaskName={TaskName}, CommandName={CommandName}, TimeoutSeconds={TimeoutSeconds}",
            jobRunId,
            job.Id,
            job.Name,
            commandName,
            job.TimeoutSeconds);

        try
        {
            // UseShellExecute=false にすることで、標準出力と標準エラーを取得できます。
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("プロセスを開始できませんでした。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = await outputTask;
                var errorOutput = await errorTask;
                var status = process.ExitCode == job.SuccessExitCode ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;

                await CompleteJobAsync(jobRunId, status, process.ExitCode, output, errorOutput, stoppingToken);
                stopwatch.Stop();
                logger.LogInformation(
                    "タスク実行を終了しました。JobRunId={JobRunId}, JobDefinitionId={JobDefinitionId}, Status={Status}, ExitCode={ExitCode}, ElapsedMilliseconds={ElapsedMilliseconds}",
                    jobRunId,
                    job.Id,
                    status,
                    process.ExitCode,
                    stopwatch.ElapsedMilliseconds);

                return status;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // アプリ停止ではなくタイムアウトの場合は、子プロセスも含めて終了させます。
                process.Kill(entireProcessTree: true);
                await CompleteJobAsync(jobRunId, ExecutionStatus.TimedOut, null, string.Empty, "タイムアウトしました。", stoppingToken);
                stopwatch.Stop();
                logger.LogWarning(
                    "タスク実行がタイムアウトしました。JobRunId={JobRunId}, JobDefinitionId={JobDefinitionId}, TimeoutSeconds={TimeoutSeconds}, ElapsedMilliseconds={ElapsedMilliseconds}",
                    jobRunId,
                    job.Id,
                    job.TimeoutSeconds,
                    stopwatch.ElapsedMilliseconds);

                return ExecutionStatus.TimedOut;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            logger.LogWarning(
                exception,
                "タスクの起動または実行に失敗しました。JobRunId={JobRunId}, JobDefinitionId={JobDefinitionId}, TaskName={TaskName}, CommandName={CommandName}, ElapsedMilliseconds={ElapsedMilliseconds}",
                jobRunId,
                job.Id,
                job.Name,
                commandName,
                stopwatch.ElapsedMilliseconds);

            await CompleteJobAsync(jobRunId, ExecutionStatus.Failed, null, string.Empty, ToJapaneseExecutionError(exception), stoppingToken);
            return ExecutionStatus.Failed;
        }
    }

    private static ProcessStartInfo CreateStartInfo(JobDefinition job)
    {
        var executablePath = job.Command.Trim().Trim('"');
        var extension = Path.GetExtension(executablePath);
        var isBatch = extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var resolvedExecutablePath = ResolveExecutablePath(executablePath);

        // 作業フォルダ未指定時は、実行ファイルの配置フォルダを既定にします。
        var workingDirectory = string.IsNullOrWhiteSpace(job.WorkingDirectory)
            ? Path.GetDirectoryName(resolvedExecutablePath)
            : job.WorkingDirectory;

        return new ProcessStartInfo
        {
            // bat/cmd は直接起動せず、cmd.exe 経由で実行します。
            FileName = isBatch ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : resolvedExecutablePath,
            Arguments = isBatch
                ? $"/d /c \"{resolvedExecutablePath}\" {job.Arguments ?? string.Empty}".TrimEnd()
                : job.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static string ResolveExecutablePath(string executablePath)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(executablePath)))
        {
            // パスが指定されている場合は絶対パスへ正規化します。
            return Path.GetFullPath(executablePath);
        }

        // ファイル名だけ指定された場合は PATH から実行ファイルを探します。
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), executablePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return executablePath;
    }

    private async Task MarkSkippedAsync(int jobRunId, string reason, CancellationToken cancellationToken) =>
        await UpdateJobRunAsync(jobRunId, item =>
        {
            item.Status = ExecutionStatus.Skipped;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.ErrorOutput = reason;
        }, cancellationToken);

    private async Task CompleteJobAsync(
        int jobRunId,
        ExecutionStatus status,
        int? exitCode,
        string output,
        string errorOutput,
        CancellationToken cancellationToken) =>
        await UpdateJobRunAsync(jobRunId, item =>
        {
            item.Status = status;
            item.ExitCode = exitCode;
            item.Output = Truncate(output);
            item.ErrorOutput = Truncate(errorOutput);
            item.CompletedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);

    private async Task UpdateJobRunAsync(int jobRunId, Action<JobRun> update, CancellationToken cancellationToken)
    {
        // 実行中のプロセスとは別タイミングで履歴を更新するため、短命の DbContext を都度作ります。
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var jobRun = await db.JobRuns.SingleAsync(item => item.Id == jobRunId, cancellationToken);
        update(jobRun);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string SafeCommandName(string command)
    {
        var text = command.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? "-" : Path.GetFileName(text);
    }

    private static string? Truncate(string? text) =>
        text?.Length > MaxStoredOutputLength ? text[..MaxStoredOutputLength] : text;

    private static string ToJapaneseExecutionError(Exception exception) => exception switch
    {
        InvalidOperationException => exception.Message,
        _ => "タスクの起動または実行に失敗しました。実行ファイルのパス、作業ディレクトリ、権限、実行環境を確認してください。"
    };
}
