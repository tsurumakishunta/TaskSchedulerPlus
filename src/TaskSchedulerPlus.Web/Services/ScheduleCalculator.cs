using TaskSchedulerPlus.Web.Data;

namespace TaskSchedulerPlus.Web.Services;

// ワークフローのスケジュール設定から、次回実行予定日時を計算します。
public static class ScheduleCalculator
{
    public static DateTimeOffset? CalculateNextRun(JobFlow workflow, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        if (!workflow.ScheduleEnabled || workflow.ScheduledStartAt is null)
        {
            // スケジュール未使用、または開始日時未設定の場合は次回実行なしです。
            return null;
        }

        // スケジュール種別ごとに計算方法を分けます。
        var nextRun = workflow.ScheduleType switch
        {
            ScheduleType.Once => CalculateOnceNextRun(workflow, referenceUtc, timeZone),
            ScheduleType.IntervalMinutes => CalculateIntervalNextRun(workflow, referenceUtc),
            ScheduleType.Daily => CalculateDailyNextRun(workflow, referenceUtc, timeZone),
            ScheduleType.Weekly => CalculateWeeklyNextRun(workflow, referenceUtc, timeZone),
            ScheduleType.Monthly => CalculateMonthlyNextRun(workflow, referenceUtc, timeZone),
            _ => workflow.ScheduledStartAt
        };

        return IsWithinEnd(workflow, nextRun) ? nextRun : null;
    }

    public static int DayMask(DayOfWeek day) => 1 << (int)day;

    public static bool IncludesDay(int mask, DayOfWeek day) => (mask & DayMask(day)) != 0;

    private static DateTimeOffset? CalculateOnceNextRun(JobFlow workflow, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        var start = workflow.ScheduledStartAt!.Value;
        return NextRepeatedOccurrence(workflow, start, referenceUtc, timeZone);
    }

    private static DateTimeOffset CalculateIntervalNextRun(JobFlow workflow, DateTimeOffset referenceUtc)
    {
        var start = workflow.ScheduledStartAt!.Value;
        if (start > referenceUtc)
        {
            // 初回開始時刻が未来なら、その時刻をそのまま次回実行にします。
            return start;
        }

        // 経過時間から次の間隔境界を求めます。
        var interval = TimeSpan.FromMinutes(Math.Max(1, workflow.ScheduleIntervalMinutes ?? 60));
        var elapsedTicks = referenceUtc.UtcTicks - start.UtcTicks;
        var intervals = elapsedTicks / interval.Ticks + 1;
        return start.AddTicks(intervals * interval.Ticks);
    }

    private static DateTimeOffset? CalculateDailyNextRun(JobFlow workflow, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        var startLocal = TimeZoneInfo.ConvertTime(workflow.ScheduledStartAt!.Value, timeZone);
        var referenceLocal = TimeZoneInfo.ConvertTime(referenceUtc, timeZone);
        var everyDays = Math.Max(1, workflow.ScheduleEveryDays);

        for (var dayOffset = 0; dayOffset < 3700; dayOffset++)
        {
            // 無限ループを避けるため、約10年分の候補日を上限に探索します。
            var candidateDate = referenceLocal.Date.AddDays(dayOffset);
            if (candidateDate < startLocal.DateTime.Date)
            {
                continue;
            }

            var daysFromStart = (candidateDate - startLocal.DateTime.Date).Days;
            if (daysFromStart % everyDays != 0)
            {
                continue;
            }

            var baseOccurrence = ToUtc(candidateDate.Add(startLocal.TimeOfDay), timeZone);
            var candidate = NextRepeatedOccurrence(workflow, baseOccurrence, referenceUtc, timeZone);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static DateTimeOffset? CalculateWeeklyNextRun(JobFlow workflow, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        var startLocal = TimeZoneInfo.ConvertTime(workflow.ScheduledStartAt!.Value, timeZone);
        var referenceLocal = TimeZoneInfo.ConvertTime(referenceUtc, timeZone);
        var mask = workflow.ScheduleDaysOfWeek == 0
            ? DayMask(startLocal.DayOfWeek)
            : workflow.ScheduleDaysOfWeek;
        var everyWeeks = Math.Max(1, workflow.ScheduleEveryWeeks);
        var startWeek = StartOfWeek(startLocal.DateTime.Date);

        for (var dayOffset = 0; dayOffset < 3700; dayOffset++)
        {
            // 対象曜日かつ週の繰り返し間隔に合う日を探します。
            var candidateDate = referenceLocal.Date.AddDays(dayOffset);
            if (candidateDate < startLocal.DateTime.Date || !IncludesDay(mask, candidateDate.DayOfWeek))
            {
                continue;
            }

            var weeksFromStart = (candidateDate - startWeek).Days / 7;
            if (weeksFromStart % everyWeeks != 0)
            {
                continue;
            }

            var baseOccurrence = ToUtc(candidateDate.Add(startLocal.TimeOfDay), timeZone);
            var candidate = NextRepeatedOccurrence(workflow, baseOccurrence, referenceUtc, timeZone);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static DateTimeOffset? CalculateMonthlyNextRun(JobFlow workflow, DateTimeOffset referenceUtc, TimeZoneInfo timeZone)
    {
        var startLocal = TimeZoneInfo.ConvertTime(workflow.ScheduledStartAt!.Value, timeZone);
        var referenceLocal = TimeZoneInfo.ConvertTime(referenceUtc, timeZone);
        var everyMonths = Math.Max(1, workflow.ScheduleEveryMonths);
        var dayOfMonth = Math.Clamp(workflow.ScheduleDayOfMonth ?? startLocal.Day, 1, 31);

        for (var monthOffset = 0; monthOffset < 1200; monthOffset++)
        {
            // 月次は約100年分を上限に、繰り返し月と日付を満たす候補を探します。
            var monthCursor = new DateTime(referenceLocal.Year, referenceLocal.Month, 1).AddMonths(monthOffset);
            var monthsFromStart = (monthCursor.Year - startLocal.Year) * 12 + monthCursor.Month - startLocal.Month;
            if (monthsFromStart < 0 || monthsFromStart % everyMonths != 0)
            {
                continue;
            }

            var actualDay = Math.Min(dayOfMonth, DateTime.DaysInMonth(monthCursor.Year, monthCursor.Month));
            var candidateLocal = new DateTime(monthCursor.Year, monthCursor.Month, actualDay).Add(startLocal.TimeOfDay);
            if (candidateLocal < startLocal.DateTime)
            {
                continue;
            }

            var baseOccurrence = ToUtc(candidateLocal, timeZone);
            var candidate = NextRepeatedOccurrence(workflow, baseOccurrence, referenceUtc, timeZone);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static DateTimeOffset? NextRepeatedOccurrence(
        JobFlow workflow,
        DateTimeOffset baseOccurrenceUtc,
        DateTimeOffset referenceUtc,
        TimeZoneInfo timeZone)
    {
        if (!workflow.RepeatEnabled)
        {
            // 繰り返しなしの場合は、基準日時より未来の1回だけを返します。
            return baseOccurrenceUtc > referenceUtc && IsWithinEnd(workflow, baseOccurrenceUtc)
                ? baseOccurrenceUtc
                : null;
        }

        // 1日の中で繰り返す設定の場合、繰り返し範囲内の次の時刻を計算します。
        var intervalMinutes = Math.Max(1, workflow.RepeatIntervalMinutes ?? 60);
        var durationMinutes = Math.Max(intervalMinutes, workflow.RepeatDurationMinutes ?? intervalMinutes);
        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var duration = TimeSpan.FromMinutes(durationMinutes);
        var baseLocal = TimeZoneInfo.ConvertTime(baseOccurrenceUtc, timeZone).DateTime;
        var referenceLocal = TimeZoneInfo.ConvertTime(referenceUtc, timeZone).DateTime;
        var repeatEndLocal = baseLocal.Add(duration);

        if (referenceLocal >= repeatEndLocal)
        {
            return null;
        }

        var candidateLocal = baseLocal;
        if (candidateLocal <= referenceLocal)
        {
            var elapsedTicks = referenceLocal.Ticks - baseLocal.Ticks;
            var intervals = elapsedTicks / interval.Ticks + 1;
            candidateLocal = baseLocal.AddTicks(intervals * interval.Ticks);
        }

        if (candidateLocal > repeatEndLocal)
        {
            return null;
        }

        var candidateUtc = ToUtc(candidateLocal, timeZone);
        return IsWithinEnd(workflow, candidateUtc) ? candidateUtc : null;
    }

    private static bool IsWithinEnd(JobFlow workflow, DateTimeOffset? candidate) =>
        candidate is not null && (workflow.ScheduleEndAt is null || candidate <= workflow.ScheduleEndAt);

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = (int)date.DayOfWeek;
        return date.AddDays(-offset);
    }

    private static DateTimeOffset ToUtc(DateTime localTime, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified))
        {
            // サマータイム等で存在しない時刻は、次の有効時刻へ寄せます。
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(unspecified, timeZone.GetUtcOffset(unspecified)).ToUniversalTime();
    }
}
