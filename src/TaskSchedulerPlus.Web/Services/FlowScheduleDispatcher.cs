using TaskSchedulerPlus.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace TaskSchedulerPlus.Web.Services;

// スケジュール時刻を過ぎたワークフローを検出し、実行キューへ登録する常駐サービスです。
public sealed class FlowScheduleDispatcher(
    ISetupConfigurationStore setupConfiguration,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IFlowExecutionQueue executionQueue,
    ILogger<FlowScheduleDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "スケジュール監視サービスを開始しました。PollingIntervalSeconds={PollingIntervalSeconds}",
            PollingInterval.TotalSeconds);

        try
        {
            using var timer = new PeriodicTimer(PollingInterval);
            while (!stoppingToken.IsCancellationRequested)
            {
                await DispatchDueFlowsAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // アプリケーション停止時のキャンセルは通常終了として扱います。
        }
        finally
        {
            logger.LogInformation("スケジュール監視サービスを停止しました。");
        }
    }

    private async Task DispatchDueFlowsAsync(CancellationToken cancellationToken)
    {
        if (!setupConfiguration.IsConfigured)
        {
            // 初期設定前は接続先 DB がないため、スケジュール監視を止めます。
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var timeZone = ResolveTimeZone();
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            // NextRunAt が現在時刻以前の有効ワークフローだけを対象にします。
            var dueFlows = await db.JobFlows
                .Where(flow => flow.IsEnabled &&
                    flow.ScheduleEnabled &&
                    flow.NextRunAt != null &&
                    flow.NextRunAt <= now)
                .OrderBy(flow => flow.NextRunAt)
                .ToListAsync(cancellationToken);

            if (dueFlows.Count > 0)
            {
                logger.LogInformation(
                    "スケジュール実行対象のワークフローを検出しました。DueFlowCount={DueFlowCount}, CheckedAt={CheckedAt}",
                    dueFlows.Count,
                    now);
            }

            foreach (var flow in dueFlows)
            {
                // キュー登録に失敗した場合に戻せるよう、更新前の値を保持します。
                var previousNextRunAt = flow.NextRunAt;
                var previousScheduleEnabled = flow.ScheduleEnabled;

                logger.LogInformation(
                    "スケジュール実行の登録を開始します。FlowId={FlowId}, WorkflowName={WorkflowName}, ScheduledNextRunAt={ScheduledNextRunAt}",
                    flow.Id,
                    flow.Name,
                    previousNextRunAt);

                flow.LastScheduledRunQueuedAt = now;
                flow.NextRunAt = ScheduleCalculator.CalculateNextRun(flow, now, timeZone);
                if (flow.NextRunAt is null)
                {
                    // 次回実行が計算できない場合は、これ以上スケジュール監視対象にしません。
                    flow.ScheduleEnabled = false;
                }

                await db.SaveChangesAsync(cancellationToken);

                try
                {
                    var runId = await executionQueue.EnqueueAsync(flow.Id, requestedByUserId: null, cancellationToken);
                    logger.LogInformation(
                        "ワークフローをスケジュールによりキューへ投入しました。FlowId={FlowId}, RunId={RunId}, PreviousNextRunAt={PreviousNextRunAt}, NextRunAt={NextRunAt}, ScheduleEnabled={ScheduleEnabled}",
                        flow.Id,
                        runId,
                        previousNextRunAt,
                        flow.NextRunAt,
                        flow.ScheduleEnabled);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // キュー登録に失敗した場合、次回の監視で再試行できるよう状態を戻します。
                    flow.LastScheduledRunQueuedAt = null;
                    flow.ScheduleEnabled = previousScheduleEnabled;
                    flow.NextRunAt = previousNextRunAt;
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogError(
                        exception,
                        "ワークフローのスケジュール実行登録に失敗しました。FlowId={FlowId}, PreviousNextRunAt={PreviousNextRunAt}",
                        flow.Id,
                        previousNextRunAt);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "ワークフロースケジュールの確認に失敗しました。");
        }
    }

    private TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            // 初期設定のタイムゾーンを優先し、取得できない場合はローカルに戻します。
            return TimeZoneInfo.FindSystemTimeZoneById(setupConfiguration.Current?.TimeZoneId ?? TimeZoneInfo.Local.Id);
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
}
