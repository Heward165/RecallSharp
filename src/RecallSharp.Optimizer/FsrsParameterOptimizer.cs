namespace RecallSharp.Optimizer;

/// <summary>
/// Fits FSRS-6 parameters to binary recall outcomes using deterministic bounded
/// coordinate descent. It is designed for portable, moderate-size datasets; large
/// datasets may use an external tensor optimizer and pass its 21 weights to RecallSharp.
/// </summary>
public sealed class FsrsParameterOptimizer
{
    private const double ProbabilityFloor = 1e-9;
    private readonly FsrsOptimizerOptions options;

    /// <summary>Creates an optimizer.</summary>
    public FsrsParameterOptimizer(FsrsOptimizerOptions? options = null)
    {
        this.options = options ?? new();
        if (this.options.MaximumPasses < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(this.options.InitialRelativeStep) || this.options.InitialRelativeStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(this.options.MinimumRelativeStep) ||
            this.options.MinimumRelativeStep <= 0 ||
            this.options.MinimumRelativeStep > this.options.InitialRelativeStep)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(this.options.Regularization) || this.options.Regularization < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (this.options.MinimumPredictiveReviews < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>Fits parameters from chronological per-card review histories.</summary>
    public FsrsOptimizationResult Optimize(
        IReadOnlyList<FsrsTrainingItem> items,
        IReadOnlyList<double>? initialParameters = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateItems(items);

        double[] defaults = FsrsOptions.DefaultParameters.ToArray();
        double[] current = (initialParameters ?? defaults).ToArray();
        if (current.Length != 21 || current.Any(value => !double.IsFinite(value)) || current[20] <= 0)
            throw new ArgumentException("An FSRS-6 parameter set must contain 21 finite weights with positive decay.", nameof(initialParameters));

        (double baseline, int samples) = Loss(items, current, defaults);
        if (samples < options.MinimumPredictiveReviews)
            throw new ArgumentException($"At least {options.MinimumPredictiveReviews} predictive reviews are required.", nameof(items));

        double best = baseline;
        double step = options.InitialRelativeStep;
        int evaluations = 1;
        int passes = 0;

        while (passes < options.MaximumPasses && step >= options.MinimumRelativeStep)
        {
            bool improved = false;
            for (int index = 0; index < current.Length; index++)
            {
                double original = current[index];
                double scale = Math.Max(Math.Abs(original), 0.1);
                foreach (int direction in new[] { -1, 1 })
                {
                    double candidate = original + (direction * scale * step);
                    if (index == 20 && candidate <= 0.001)
                        continue;

                    current[index] = candidate;
                    (double loss, _) = Loss(items, current, defaults);
                    evaluations++;
                    if (loss + 1e-12 < best)
                    {
                        best = loss;
                        original = candidate;
                        improved = true;
                    }
                    else
                    {
                        current[index] = original;
                    }
                }

                current[index] = original;
            }

            passes++;
            if (!improved)
                step *= 0.5;
        }

        return new(Array.AsReadOnly(current), baseline, best, samples, evaluations, passes);
    }

    private (double Loss, int Samples) Loss(
        IReadOnlyList<FsrsTrainingItem> items,
        IReadOnlyList<double> parameters,
        IReadOnlyList<double> defaults)
    {
        FsrsOptions schedulerOptions = FsrsOptions.Default with
        {
            Parameters = parameters,
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        };
        var scheduler = new Fsrs6Scheduler(schedulerOptions);
        double total = 0;
        int samples = 0;

        foreach (FsrsTrainingItem item in items)
        {
            FsrsMemoryState state = FsrsMemoryState.New(item.CardId, item.Reviews[0].ReviewedAt);
            foreach (FsrsReviewObservation review in item.Reviews)
            {
                if (state.LastReviewAt is not null)
                {
                    double probability = Math.Clamp(
                        scheduler.Retrievability(state, review.ReviewedAt),
                        ProbabilityFloor,
                        1 - ProbabilityFloor);
                    bool recalled = review.Rating != FsrsRating.Again;
                    total -= recalled ? Math.Log(probability) : Math.Log(1 - probability);
                    samples++;
                }

                state = scheduler.Review(state, review.Rating, review.ReviewedAt).Current;
            }
        }

        double penalty = 0;
        for (int index = 0; index < parameters.Count; index++)
        {
            double scale = Math.Max(Math.Abs(defaults[index]), 0.1);
            penalty += Math.Pow((parameters[index] - defaults[index]) / scale, 2);
        }

        return ((total / Math.Max(samples, 1)) + (options.Regularization * penalty), samples);
    }

    private static void ValidateItems(IReadOnlyList<FsrsTrainingItem> items)
    {
        if (items.Count == 0)
            throw new ArgumentException("At least one training item is required.", nameof(items));

        var cardIds = new HashSet<long>();
        foreach (FsrsTrainingItem item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Reviews);
            if (!cardIds.Add(item.CardId))
                throw new ArgumentException("Training card identifiers must be unique.", nameof(items));
            if (item.Reviews.Count < 2)
                throw new ArgumentException("Every training item requires at least two reviews.", nameof(items));

            DateTimeOffset previous = default;
            foreach (FsrsReviewObservation review in item.Reviews)
            {
                ArgumentNullException.ThrowIfNull(review);
                if (review.ReviewedAt == default || (previous != default && review.ReviewedAt < previous))
                    throw new ArgumentException("Reviews must contain valid chronological timestamps.", nameof(items));
                if (review.Rating is < FsrsRating.Again or > FsrsRating.Easy)
                    throw new ArgumentException("Reviews contain an invalid rating.", nameof(items));
                previous = review.ReviewedAt;
            }
        }
    }
}
