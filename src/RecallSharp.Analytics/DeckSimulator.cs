namespace RecallSharp.Analytics;

/// <summary>Defines one reproducible workload simulation.</summary>
public sealed record DeckSimulationDefinition
{
    /// <summary>UTC instant representing the start of day zero.</summary>
    public DateTimeOffset StartAt { get; init; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Number of simulated days.</summary>
    public int Days { get; init; } = 365;

    /// <summary>Maximum number of cards introduced during the run.</summary>
    public int DeckSize { get; init; } = 1_000;

    /// <summary>New cards introduced at the beginning of each day.</summary>
    public int NewCardsPerDay { get; init; } = 10;

    /// <summary>Maximum reviews performed on one day.</summary>
    public int MaximumReviewsPerDay { get; init; } = 500;

    /// <summary>Expected active study time per review.</summary>
    public double SecondsPerReview { get; init; } = 8;

    /// <summary>Probability that an introduced card is recalled on its first simulated review.</summary>
    public double NewCardRecallProbability { get; init; } = 0.75;

    /// <summary>Deterministic behavior seed.</summary>
    public ulong Seed { get; init; } = 0x53494D554C415445UL;
}

/// <summary>One day's workload and expected memory.</summary>
public sealed record DeckSimulationDay(
    int Day,
    int IntroducedCards,
    int Reviews,
    int Lapses,
    double StudyMinutes,
    double ExpectedRememberedCards);

/// <summary>Aggregate and raw outputs for one retention target.</summary>
public sealed record DeckSimulationReport(
    double DesiredRetention,
    int TotalCardsIntroduced,
    int TotalReviews,
    int TotalLapses,
    double TotalStudyMinutes,
    double ExpectedRememberedCards,
    double ReviewsPerExpectedRememberedCard,
    double MeanDailyReviews,
    int PeakDailyReviews,
    int P95DailyReviews,
    IReadOnlyList<DeckSimulationDay> Days);

/// <summary>Runs deterministic, deck-scale FSRS workload experiments.</summary>
public static class DeckSimulator
{
    /// <summary>Simulates one desired-retention target.</summary>
    public static DeckSimulationReport Run(
        DeckSimulationDefinition definition,
        double desiredRetention,
        IReadOnlyList<double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition, desiredRetention);
        var scheduler = new FsrsScheduler(FsrsOptions.Default with
        {
            DesiredRetention = desiredRetention,
            Parameters = parameters ?? FsrsOptions.DefaultParameters,
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        });
        var cards = new List<FsrsMemoryState>(definition.DeckSize);
        var days = new List<DeckSimulationDay>(definition.Days);
        ulong random = definition.Seed;
        int nextCardId = 1;
        int totalReviews = 0;
        int totalLapses = 0;

        for (int day = 0; day < definition.Days; day++)
        {
            DateTimeOffset now = definition.StartAt.ToUniversalTime().AddDays(day);
            int introduced = Math.Min(definition.NewCardsPerDay, definition.DeckSize - cards.Count);
            for (int count = 0; count < introduced; count++)
                cards.Add(FsrsMemoryState.New(nextCardId++, now));

            int reviews = 0;
            int lapses = 0;
            int[] dueIndices = cards
                .Select((state, index) => (state, index))
                .Where(item => item.state.DueAt <= now)
                .OrderBy(item => item.state.DueAt)
                .ThenBy(item => item.state.CardId)
                .Take(definition.MaximumReviewsPerDay)
                .Select(item => item.index)
                .ToArray();
            foreach (int index in dueIndices)
            {
                FsrsMemoryState state = cards[index];
                double recall = state.Stability is null
                    ? definition.NewCardRecallProbability
                    : scheduler.Retrievability(state, now);
                FsrsRating rating = DrawRating(recall, ref random);
                FsrsReviewResult result = scheduler.Review(state, rating, now);
                cards[index] = result.Current;
                reviews++;
                if (rating == FsrsRating.Again && state.Stage == MemoryStage.Review) lapses++;
            }

            totalReviews += reviews;
            totalLapses += lapses;
            double remembered = cards.Sum(card => card.Stability is null ? 0 : scheduler.Retrievability(card, now));
            days.Add(new DeckSimulationDay(
                day,
                introduced,
                reviews,
                lapses,
                reviews * definition.SecondsPerReview / 60,
                remembered));
        }

        DateTimeOffset end = definition.StartAt.ToUniversalTime().AddDays(definition.Days);
        double expectedRemembered = cards.Sum(card =>
            card.Stability is null ? 0 : scheduler.Retrievability(card, end));
        int[] workloads = days.Select(day => day.Reviews).Order().ToArray();
        int p95 = workloads[(int)Math.Ceiling(0.95 * workloads.Length) - 1];
        return new DeckSimulationReport(
            desiredRetention,
            cards.Count,
            totalReviews,
            totalLapses,
            totalReviews * definition.SecondsPerReview / 60,
            expectedRemembered,
            expectedRemembered == 0 ? double.PositiveInfinity : totalReviews / expectedRemembered,
            days.Average(day => day.Reviews),
            workloads[^1],
            p95,
            days);
    }

    private static FsrsRating DrawRating(double recall, ref ulong random)
    {
        double draw = NextDouble(ref random);
        if (draw > recall) return FsrsRating.Again;
        double fluency = NextDouble(ref random);
        if (fluency < 0.12 + (0.18 * (1 - recall))) return FsrsRating.Hard;
        if (fluency > 0.90 - (0.20 * recall)) return FsrsRating.Easy;
        return FsrsRating.Good;
    }

    private static void Validate(DeckSimulationDefinition definition, double retention)
    {
        if (definition.StartAt == default || definition.Days < 1 || definition.DeckSize < 1 ||
            definition.NewCardsPerDay < 0 || definition.MaximumReviewsPerDay < 1 ||
            !double.IsFinite(definition.SecondsPerReview) || definition.SecondsPerReview <= 0 ||
            !double.IsFinite(definition.NewCardRecallProbability) ||
            definition.NewCardRecallProbability is <= 0 or >= 1 ||
            !double.IsFinite(retention) || retention is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(definition));
    }

    private static double NextDouble(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (value >> 11) * (1.0 / (1UL << 53));
    }
}
