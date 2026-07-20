namespace RecallSharp.Analytics;

/// <summary>Weights knowledge against study time when selecting retention.</summary>
public sealed record RetentionObjective(
    double StudyMinuteCost = 0.25,
    double RememberedCardValue = 1,
    int? MaximumPeakDailyReviews = null);

/// <summary>One candidate's simulation and scalar utility.</summary>
public sealed record RetentionCandidate(
    double DesiredRetention,
    double Utility,
    bool Feasible,
    DeckSimulationReport Simulation);

/// <summary>Auditable grid search for an appropriate desired retention.</summary>
public sealed record OptimalRetentionReport(
    RetentionCandidate Best,
    IReadOnlyList<RetentionCandidate> Candidates);

/// <summary>Selects retention from explicit workload and knowledge assumptions.</summary>
public static class OptimalRetentionCalculator
{
    /// <summary>Evaluates each unique candidate and returns the highest-utility feasible value.</summary>
    public static OptimalRetentionReport Evaluate(
        DeckSimulationDefinition simulation,
        IEnumerable<double> candidateRetentions,
        RetentionObjective? objective = null,
        IReadOnlyList<double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(candidateRetentions);
        RetentionObjective target = objective ?? new RetentionObjective();
        if (!double.IsFinite(target.StudyMinuteCost) || target.StudyMinuteCost < 0 ||
            !double.IsFinite(target.RememberedCardValue) || target.RememberedCardValue <= 0 ||
            target.MaximumPeakDailyReviews < 1)
            throw new ArgumentOutOfRangeException(nameof(objective));

        double[] candidates = candidateRetentions.Distinct().Order().ToArray();
        if (candidates.Length == 0) throw new ArgumentException("At least one retention is required.", nameof(candidateRetentions));
        var results = candidates.Select(retention =>
        {
            DeckSimulationReport report = DeckSimulator.Run(simulation, retention, parameters);
            bool feasible = target.MaximumPeakDailyReviews is null ||
                            report.PeakDailyReviews <= target.MaximumPeakDailyReviews;
            double utility = (target.RememberedCardValue * report.ExpectedRememberedCards) -
                             (target.StudyMinuteCost * report.TotalStudyMinutes);
            return new RetentionCandidate(retention, utility, feasible, report);
        }).ToArray();
        RetentionCandidate best = results.Where(result => result.Feasible)
            .OrderByDescending(result => result.Utility)
            .ThenBy(result => result.DesiredRetention)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No retention candidate satisfies the workload constraint.");
        return new OptimalRetentionReport(best, results);
    }
}
