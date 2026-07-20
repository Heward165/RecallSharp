namespace RecallSharp.Policy;

/// <summary>Scheduling preferences applied after the memory model has produced an interval.</summary>
public sealed record FsrsPolicyOptions
{
    /// <summary>Lower bound for model intervals that remain on the same day.</summary>
    public TimeSpan MinimumSameDayInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Maximum permitted growth relative to the preceding scheduled interval.</summary>
    public double MaximumIntervalGrowth { get; init; } = 10;

    /// <summary>Absolute policy interval ceiling.</summary>
    public TimeSpan MaximumInterval { get; init; } = TimeSpan.FromDays(36_500);

    /// <summary>Days searched around the model date for a less disruptive due date.</summary>
    public int BalancingWindowDays { get; init; } = 3;

    /// <summary>Preferred review weekdays; an empty collection treats every day equally.</summary>
    public IReadOnlySet<DayOfWeek> PreferredWeekdays { get; init; } = new HashSet<DayOfWeek>();

    /// <summary>Maximum projected reviews accepted on a day before overflow is penalized.</summary>
    public int MaximumReviewsPerDay { get; init; } = int.MaxValue;

    /// <summary>Minimum distance from a sibling card's due date.</summary>
    public TimeSpan SiblingSeparation { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>External workload facts used by the policy without contaminating memory prediction.</summary>
public sealed record FsrsPolicyContext(
    DateTimeOffset ReviewedAt,
    TimeSpan? PreviousInterval = null,
    IReadOnlyDictionary<DateOnly, int>? ProjectedDailyReviews = null,
    IReadOnlyList<DateTimeOffset>? SiblingDueDates = null);

/// <summary>Explains a deterministic due-date adjustment.</summary>
public sealed record FsrsPolicyDecision(
    DateTimeOffset ModelDueAt,
    DateTimeOffset PolicyDueAt,
    IReadOnlyList<string> AppliedRules)
{
    /// <summary>True when policy changed the model's due instant.</summary>
    public bool WasAdjusted => ModelDueAt != PolicyDueAt;
}

/// <summary>
/// Applies workload preferences to a model result while leaving the predicted memory state untouched.
/// Persist the model state separately and store the policy due date in the host application's queue.
/// </summary>
public static class FsrsSchedulingPolicy
{
    /// <summary>Chooses a deterministic policy due date near the model's due date.</summary>
    public static FsrsPolicyDecision Apply(
        FsrsReviewResult modelResult,
        FsrsPolicyContext context,
        FsrsPolicyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelResult);
        ArgumentNullException.ThrowIfNull(context);
        FsrsPolicyOptions configured = options ?? new FsrsPolicyOptions();
        Validate(configured);
        if (context.ReviewedAt.ToUniversalTime() != modelResult.ReviewedAt.ToUniversalTime())
            throw new ArgumentException("Policy context and model review timestamps must match.", nameof(context));

        var rules = new List<string>();
        TimeSpan interval = modelResult.Interval;
        if (interval < TimeSpan.FromDays(1) && interval < configured.MinimumSameDayInterval)
        {
            interval = configured.MinimumSameDayInterval;
            rules.Add("minimum-same-day-interval");
        }

        if (context.PreviousInterval is { } previous && previous > TimeSpan.Zero)
        {
            TimeSpan growthLimit = TimeSpan.FromTicks((long)Math.Min(
                TimeSpan.MaxValue.Ticks,
                previous.Ticks * configured.MaximumIntervalGrowth));
            if (interval > growthLimit)
            {
                interval = growthLimit;
                rules.Add("maximum-interval-growth");
            }
        }

        if (interval > configured.MaximumInterval)
        {
            interval = configured.MaximumInterval;
            rules.Add("maximum-interval");
        }

        DateTimeOffset boundedDue = context.ReviewedAt.ToUniversalTime().Add(interval);
        if (interval < TimeSpan.FromDays(1) || configured.BalancingWindowDays == 0)
            return new(modelResult.Current.DueAt, boundedDue, rules.AsReadOnly());

        DateTimeOffset selected = Enumerable.Range(-configured.BalancingWindowDays,
                (configured.BalancingWindowDays * 2) + 1)
            .Select(offset => boundedDue.AddDays(offset))
            .Where(candidate => candidate > context.ReviewedAt)
            .Select(candidate => (Due: candidate, Score: Score(candidate, boundedDue, context, configured)))
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs((candidate.Due - boundedDue).TotalDays))
            .ThenBy(candidate => candidate.Due)
            .First().Due;

        if (selected.Date != boundedDue.Date)
            rules.Add("workload-balancing");
        return new(modelResult.Current.DueAt, selected, rules.AsReadOnly());
    }

    private static double Score(
        DateTimeOffset candidate,
        DateTimeOffset modelDue,
        FsrsPolicyContext context,
        FsrsPolicyOptions options)
    {
        DateOnly day = DateOnly.FromDateTime(candidate.UtcDateTime);
        int load = context.ProjectedDailyReviews?.GetValueOrDefault(day) ?? 0;
        double score = Math.Abs((candidate - modelDue).TotalDays);
        score += load / (double)Math.Max(1, options.MaximumReviewsPerDay);
        if (load >= options.MaximumReviewsPerDay)
            score += 1_000 + load;
        if (options.PreferredWeekdays.Count > 0 && !options.PreferredWeekdays.Contains(candidate.DayOfWeek))
            score += 10;
        if (context.SiblingDueDates?.Any(due =>
                Math.Abs((due.ToUniversalTime() - candidate).TotalDays) < options.SiblingSeparation.TotalDays) == true)
            score += 100;
        return score;
    }

    private static void Validate(FsrsPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.PreferredWeekdays);
        if (options.MinimumSameDayInterval <= TimeSpan.Zero || options.MaximumInterval <= TimeSpan.Zero ||
            !double.IsFinite(options.MaximumIntervalGrowth) || options.MaximumIntervalGrowth < 1 ||
            options.BalancingWindowDays is < 0 or > 31 || options.MaximumReviewsPerDay < 1 ||
            options.SiblingSeparation < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}
