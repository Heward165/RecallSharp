namespace RecallSharp.Optimizer;

/// <summary>A recorded rating at an exact instant.</summary>
public sealed record FsrsReviewObservation(DateTimeOffset ReviewedAt, FsrsRating Rating);

/// <summary>Chronological review observations for one independently scheduled card.</summary>
public sealed record FsrsTrainingItem(long CardId, IReadOnlyList<FsrsReviewObservation> Reviews);

/// <summary>Controls deterministic coordinate-descent parameter fitting.</summary>
public sealed record FsrsOptimizerOptions(
    int MaximumPasses = 6,
    double InitialRelativeStep = 0.08,
    double MinimumRelativeStep = 0.0025,
    double Regularization = 1e-4,
    int MinimumPredictiveReviews = 20);

/// <summary>Auditable output from an optimizer run.</summary>
public sealed record FsrsOptimizationResult(
    IReadOnlyList<double> Parameters,
    double BaselineLogLoss,
    double OptimizedLogLoss,
    int PredictiveReviewCount,
    int Evaluations,
    int CompletedPasses);
