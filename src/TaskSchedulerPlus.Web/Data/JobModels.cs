using System.ComponentModel.DataAnnotations;

namespace TaskSchedulerPlus.Web.Data;

// 実行単位の現在状態を表します。
public enum ExecutionStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Skipped,
    TimedOut
}

// スケジュール設定画面で選択する実行周期です。
public enum ScheduleType
{
    Once = 0,
    Daily = 1,
    Weekly = 2,
    IntervalMinutes = 3,
    Monthly = 4
}

// 作成日・更新日を共通で持つエンティティの印です。
public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}

// exe やバッチなど、実際に起動する処理の定義です。
public class JobDefinition : IAuditableEntity
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [Required(ErrorMessage = "タスク名を入力してください。")]
    [StringLength(100, ErrorMessage = "タスク名は {1} 文字以内で入力してください。")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "説明は {1} 文字以内で入力してください。")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "実行ファイルを入力してください。")]
    [StringLength(500, ErrorMessage = "実行ファイルは {1} 文字以内で入力してください。")]
    public string Command { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "パラメーターは {1} 文字以内で入力してください。")]
    public string? Arguments { get; set; }

    [StringLength(500, ErrorMessage = "作業ディレクトリは {1} 文字以内で入力してください。")]
    public string? WorkingDirectory { get; set; }

    public int SuccessExitCode { get; set; }

    [Range(1, 86400, ErrorMessage = "タイムアウトは 1 秒から 86400 秒の範囲で入力してください。")]
    public int TimeoutSeconds { get; set; } = 300;

    public bool IsEnabled { get; set; } = true;

    public ICollection<JobNode> Nodes { get; set; } = [];
}

// 複数の処理定義を束ね、スケジュールを持つ実行単位です。
public class JobFlow : IAuditableEntity
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [Required(ErrorMessage = "ワークフロー名を入力してください。")]
    [StringLength(100, ErrorMessage = "ワークフロー名は {1} 文字以内で入力してください。")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "説明は {1} 文字以内で入力してください。")]
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

    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastScheduledRunQueuedAt { get; set; }

    public ICollection<JobNode> Nodes { get; set; } = [];

    public ICollection<FlowRun> Runs { get; set; } = [];
}

// ワークフロー内での個別タスク配置を表します。
// 画面や外部参照で扱いやすいよう、主キーは Guid にしています。
public class JobNode : IAuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int JobFlowId { get; set; }
    public JobFlow JobFlow { get; set; } = null!;

    public int JobDefinitionId { get; set; }
    public JobDefinition JobDefinition { get; set; } = null!;

    [Required(ErrorMessage = "タスク名を入力してください。")]
    [StringLength(100, ErrorMessage = "タスク名は {1} 文字以内で入力してください。")]
    public string Name { get; set; } = string.Empty;

    public bool RunNextOnSuccess { get; set; } = true;

    public bool StopOnFailure { get; set; } = true;

    public int DisplayOrder { get; set; }

    public ICollection<JobRun> Runs { get; set; } = [];
}

// ワークフロー1回分の実行履歴です。
public class FlowRun : IAuditableEntity
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int JobFlowId { get; set; }
    public JobFlow JobFlow { get; set; } = null!;

    [StringLength(450, ErrorMessage = "依頼ユーザーIDは {1} 文字以内で入力してください。")]
    public string? RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;

    [StringLength(1000, ErrorMessage = "メッセージは {1} 文字以内で入力してください。")]
    public string? Message { get; set; }

    public ICollection<JobRun> JobRuns { get; set; } = [];
}

// ワークフロー実行内の、個別タスク1回分の実行履歴です。
public class JobRun : IAuditableEntity
{
    public int Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int FlowRunId { get; set; }
    public FlowRun FlowRun { get; set; } = null!;

    public Guid JobNodeId { get; set; }
    public JobNode JobNode { get; set; } = null!;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;
    public int? ExitCode { get; set; }

    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }
}
