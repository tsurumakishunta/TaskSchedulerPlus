namespace TaskSchedulerPlus.Web.Services;

// ワークフロー実行要求をキューとして登録するための境界です。
public interface IFlowExecutionQueue
{
    // 戻り値は作成された実行履歴の ID です。
    Task<int> EnqueueAsync(int flowId, string? requestedByUserId, CancellationToken cancellationToken = default);
}
