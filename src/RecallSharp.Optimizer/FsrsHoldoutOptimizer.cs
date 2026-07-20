namespace RecallSharp.Optimization;

/// <summary>Controls deterministic coordinate-descent parameter fitting with held-out evaluation.</summary>
public sealed record FsrsOptimizationOptions
{
    /// <summary>Fraction of complete card histories used for chronological training.</summary>
    public double TrainingFraction { get; init; } = 0.8;

    /// <summary>Maximum full coordinate passes.</summary>
    public int MaximumPasses { get; init; } = 12;

    /// <summary>Initial multiplicative search distance in log space.</summary>
    public double InitialLogStep { get; init; } = 0.20;

    /// <summary>Stop after the search distance falls below this value.</summary>
    public double MinimumLogStep { get; init; } = 0.005;

    /// <summary>Minimum improvement accepted as meaningful.</summary>
    public double MinimumImprovement { get; init; } = 1e-7;

    /// <summary>Bootstrap refits used for parameter confidence intervals; zero disables them.</summary>
    public int BootstrapSamples { get; init; }

    /// <summary>Seed used only for reproducible bootstrap resampling.</summary>
    public ulong BootstrapSeed { get; init; } = 0x524543414C4CUL;
}

/// <summary>Bootstrap interval for one optimized parameter.</summary>
public sealed record FsrsParameterEstimate(
    int Index,
    double Value,
    double ConfidenceLower,
    double ConfidenceUpper);

/// <summary>Complete fitting result with held-out accuracy and uncertainty.</summary>
public sealed record FsrsOptimizationResult(
    IReadOnlyList<double> Parameters,
    FsrsEvaluationMetrics TrainingMetrics,
    FsrsEvaluationMetrics ValidationMetrics,
    IReadOnlyList<FsrsParameterEstimate> ParameterEstimates,
    int CompletedPasses,
    bool Converged,
    int TrainingCardCount,
    int ValidationCardCount)
{
    /// <summary>Data-quality cautions that should accompany the fitted parameters.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Fits FSRS-6 weights without native or machine-learning dependencies.
/// This transparent optimizer favors auditability over tensor-library throughput.
/// </summary>
public static class FsrsParameterOptimizer
{
    /// <summary>Fits parameters using a chronological, card-level holdout.</summary>
    public static FsrsOptimizationResult Optimize(
        IEnumerable<FsrsCardHistory> histories,
        FsrsOptimizationOptions? options = null,
        IReadOnlyList<double>? initialParameters = null)
    {
        ArgumentNullException.ThrowIfNull(histories);
        FsrsOptimizationOptions settings = options ?? new FsrsOptimizationOptions();
        Validate(settings);
        FsrsCardHistory[] ordered = histories
            .Where(history => history.Reviews.Count >= 2)
            .OrderBy(history => history.Reviews[^1].ReviewedAt)
            .ThenBy(history => history.CardId)
            .ToArray();
        if (ordered.Length < 2)
            throw new ArgumentException("Optimization requires at least two cards with repeated reviews.", nameof(histories));

        int trainingCount = Math.Clamp((int)Math.Floor(ordered.Length * settings.TrainingFraction), 1, ordered.Length - 1);
        FsrsCardHistory[] training = ordered[..trainingCount];
        FsrsCardHistory[] validation = ordered[trainingCount..];
        double[] fitted = Fit(training, settings, initialParameters, out int passes, out bool converged);
        FsrsEvaluationMetrics trainingMetrics = FsrsModelEvaluator.Evaluate(training, fitted);
        FsrsEvaluationMetrics validationMetrics = FsrsModelEvaluator.Evaluate(validation, fitted);
        IReadOnlyList<FsrsParameterEstimate> estimates = Bootstrap(training, fitted, settings);
        return new FsrsOptimizationResult(
            Array.AsReadOnly(fitted),
            trainingMetrics,
            validationMetrics,
            estimates,
            passes,
            converged,
            training.Length,
            validation.Length)
        {
            Warnings = AssessData(ordered)
        };
    }

    private static double[] Fit(
        IReadOnlyList<FsrsCardHistory> histories,
        FsrsOptimizationOptions settings,
        IReadOnlyList<double>? initial,
        out int completedPasses,
        out bool converged)
    {
        double[] parameters = (initial ?? FsrsOptions.DefaultParameters).ToArray();
        if (parameters.Length != 21 || parameters.Any(value => !double.IsFinite(value) || value <= 0))
            throw new ArgumentException("Initial FSRS-6 parameters must contain 21 positive finite values.", nameof(initial));

        double best = Loss(histories, parameters);
        double step = settings.InitialLogStep;
        converged = false;
        for (completedPasses = 1; completedPasses <= settings.MaximumPasses; completedPasses++)
        {
            bool improved = false;
            for (int index = 0; index < parameters.Length; index++)
            {
                double original = parameters[index];
                double candidateBest = best;
                double selected = original;
                foreach (double direction in new[] { -1d, 1d })
                {
                    double candidate = Math.Clamp(original * Math.Exp(direction * step), 1e-5, UpperBound(index));
                    parameters[index] = candidate;
                    double loss = Loss(histories, parameters);
                    if (loss + settings.MinimumImprovement < candidateBest)
                    {
                        candidateBest = loss;
                        selected = candidate;
                    }
                }

                parameters[index] = selected;
                if (candidateBest < best)
                {
                    best = candidateBest;
                    improved = true;
                }
            }

            if (!improved)
                step *= 0.5;
            if (step < settings.MinimumLogStep)
            {
                converged = true;
                break;
            }
        }

        completedPasses = Math.Min(completedPasses, settings.MaximumPasses);
        return parameters;
    }

    private static IReadOnlyList<FsrsParameterEstimate> Bootstrap(
        IReadOnlyList<FsrsCardHistory> training,
        double[] fitted,
        FsrsOptimizationOptions settings)
    {
        if (settings.BootstrapSamples == 0)
            return fitted.Select((value, index) => new FsrsParameterEstimate(index, value, value, value)).ToArray();

        var samples = Enumerable.Range(0, fitted.Length).Select(_ => new List<double>()).ToArray();
        ulong random = settings.BootstrapSeed;
        var bootstrapSettings = settings with
        {
            MaximumPasses = Math.Max(2, settings.MaximumPasses / 3),
            BootstrapSamples = 0
        };
        for (int sample = 0; sample < settings.BootstrapSamples; sample++)
        {
            var resampled = new FsrsCardHistory[training.Count];
            for (int index = 0; index < resampled.Length; index++)
                resampled[index] = training[(int)(Next(ref random) % (ulong)training.Count)];
            double[] estimate = Fit(resampled, bootstrapSettings, fitted, out _, out _);
            for (int index = 0; index < estimate.Length; index++)
                samples[index].Add(estimate[index]);
        }

        return fitted.Select((value, index) =>
        {
            double[] ordered = samples[index].Order().ToArray();
            return new FsrsParameterEstimate(
                index,
                value,
                Quantile(ordered, 0.025),
                Quantile(ordered, 0.975));
        }).ToArray();
    }

    private static double Loss(IReadOnlyList<FsrsCardHistory> histories, IReadOnlyList<double> parameters)
    {
        try
        {
            return FsrsModelEvaluator.Evaluate(histories, parameters).LogLoss;
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or OverflowException)
        {
            return double.MaxValue;
        }
    }

    private static double UpperBound(int index) => index switch
    {
        0 or 1 or 2 or 3 => 100,
        4 => 20,
        5 or 7 or 12 or 18 or 19 or 20 => 5,
        _ => 20
    };

    private static IReadOnlyList<string> AssessData(IReadOnlyList<FsrsCardHistory> histories)
    {
        var warnings = new List<string>();
        int observations = histories.Sum(history => Math.Max(0, history.Reviews.Count - 1));
        if (histories.Count < 100)
            warnings.Add("Fewer than 100 repeated-review cards can produce unstable personal parameters.");
        if (observations < 1_000)
            warnings.Add("Fewer than 1,000 evaluated reviews limits calibration and uncertainty estimates.");
        FsrsReviewLog[] repeated = histories.SelectMany(history => history.Reviews.Skip(1)).ToArray();
        double failureRate = repeated.Count(review => review.Rating == FsrsRating.Again) /
                             (double)Math.Max(1, repeated.Length);
        if (failureRate is < 0.03 or > 0.60)
            warnings.Add("The observed failure rate is highly imbalanced; inspect rating usage and sampling.");
        int sameDay = histories.Sum(history => history.Reviews.Zip(history.Reviews.Skip(1),
            (first, second) => (second.ReviewedAt - first.ReviewedAt).TotalDays < 1 ? 1 : 0).Sum());
        if (sameDay > observations / 2)
            warnings.Add("More than half of evaluated reviews are same-day; long-term parameters may be weakly identified.");
        return warnings;
    }

    private static void Validate(FsrsOptimizationOptions options)
    {
        if (!double.IsFinite(options.TrainingFraction) || options.TrainingFraction is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(options.TrainingFraction));
        if (options.MaximumPasses < 1 || options.InitialLogStep <= 0 || options.MinimumLogStep <= 0 ||
            options.MinimumLogStep >= options.InitialLogStep || options.MinimumImprovement < 0 ||
            options.BootstrapSamples is < 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static double Quantile(double[] values, double probability)
    {
        double position = probability * (values.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return values[lower] + ((values[upper] - values[lower]) * (position - lower));
    }
}
