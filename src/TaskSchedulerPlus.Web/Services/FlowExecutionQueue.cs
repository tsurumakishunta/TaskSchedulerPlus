using TaskSchedulerPlus.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// 画面操作やスケジューラから呼び出され、実行待ちの履歴レコードを作成します。
public sealed class FlowExecutionQueue(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ILogger<FlowExecutionQueue> logger) : IFlowExecutionQueue
{
    public async Task<int> EnqueueAsync(int flowId, string? requestedByUserId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "ワークフローを実行キューへ登録します。FlowId={FlowId}, RequestedByUserId={RequestedByUserId}",
            flowId,
            requestedByUserId ?? "system");

        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // 無効なワークフローはキューに積ませず、呼び出し元で例外として扱わせます。
            var flow = await db.JobFlows
                .Include(item => item.Nodes)
                .SingleAsync(item => item.Id == flowId && item.IsEnabled, cancellationToken);

            // ワークフロー実行と同時に、各タスクの実行履歴を表示順で作成します。
            var run = new FlowRun
            {
                JobFlowId = flow.Id,
                RequestedByUserId = requestedByUserId,
                RequestedAt = DateTimeOffset.UtcNow,
                Status = ExecutionStatus.Queued,
                JobRuns = flow.Nodes
                    .OrderBy(node => node.DisplayOrder)
                    .Select(node => new JobRun { JobNodeId = node.Id })
                    .ToList()
            };

            db.FlowRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "ワークフローを実行キューへ登録しました。FlowId={FlowId}, RunId={RunId}, TaskCount={TaskCount}",
                flow.Id,
                run.Id,
                run.JobRuns.Count);

            return run.Id;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "ワークフローの実行キュー登録に失敗しました。FlowId={FlowId}", flowId);
            throw;
        }
    }
}
