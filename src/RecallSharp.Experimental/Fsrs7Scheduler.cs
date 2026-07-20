namespace RecallSharp.Fsrs7.Experimental;

/// <summary>
/// Experimental FSRS-7 scheduler with fractional elapsed time, a mixed power-law
/// forgetting curve, and separate short- and long-term stability transitions.
/// </summary>
/// <remarks>
/// The implementation follows the model currently evaluated by the official SRS benchmark.
/// It is isolated in an experimental assembly because FSRS-7 is not yet the stable RecallSharp API.
/// </remarks>
public sealed class Fsrs7Scheduler
{
    private const double MinimumStability = 0.0001;
    private const double MaximumStability = 36_500;
    private readonly Fsrs7Options options;
    private readonly double[] w;

    /// <summary>Creates an experimental FSRS-7 scheduler.</summary>
    public Fsrs7Scheduler(Fsrs7Options? options = null)
    {
        Fsrs7Options configured = options ?? Fsrs7Options.Default;
        ArgumentNullException.ThrowIfNull(configured.Parameters);
        if (configured.Parameters.Count != 35 || configured.Parameters.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("FSRS-7 requires exactly 35 finite parameters.", nameof(options));
        if (!double.IsFinite(configured.DesiredRetention) || configured.DesiredRetention is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Desired retention must be between zero and one.");
        if (configured.MinimumInterval <= TimeSpan.Zero || configured.MaximumInterval <= configured.MinimumInterval)
            throw new ArgumentOutOfRangeException(nameof(options), "The interval bounds are invalid.");

        // Forgetting-curve parameters must keep both components finite and monotonic.
        if (configured.Parameters[27] <= 0 || configured.Parameters[28] <= 0 ||
            configured.Parameters[29] is <= 0 or >= 1 || configured.Parameters[30] is <= 0 or >= 1 ||
            configured.Parameters[31] <= 0 || configured.Parameters[32] <= 0)
            throw new ArgumentException("The FSRS-7 forgetting-curve parameters are invalid.", nameof(options));

        w = configured.Parameters.ToArray();
        this.options = configured with { Parameters = Array.AsReadOnly(w) };
    }

    /// <summary>Gets the immutable configuration snapshot.</summary>
    public Fsrs7Options Options => options;

    /// <summary>Estimates recall using fractional elapsed days, including same-day reviews.</summary>
    public double Retrievability(FsrsMemoryState state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LastReviewAt is null || state.Stability is null)
            return 0;
        if (!double.IsFinite(state.Stability.Value) || state.Stability <= 0)
            throw new ArgumentException("The state stability must be positive and finite.", nameof(state));
        if (at < state.LastReviewAt.Value)
            throw new ArgumentOutOfRangeException(nameof(at));

        double elapsedDays = (at.ToUniversalTime() - state.LastReviewAt.Value.ToUniversalTime()).TotalDays;
        return ForgettingCurve(elapsedDays, state.Stability.Value);
    }

    /// <summary>Applies a rating and returns a pure transition with a fractional interval.</summary>
    public FsrsReviewResult Review(FsrsMemoryState state, FsrsRating rating, DateTimeOffset reviewedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (rating is < FsrsRating.Again or > FsrsRating.Easy)
            throw new ArgumentOutOfRangeException(nameof(rating));
        reviewedAt = reviewedAt.ToUniversalTime();
        if (state.LastReviewAt is { } previous && reviewedAt < previous.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(reviewedAt));

        double retrievability;
        double stability;
        double difficulty;
        if (state.Stability is null || state.Difficulty is null || state.LastReviewAt is null)
        {
            retrievability = 0;
            stability = Math.Clamp(w[(int)rating - 1], MinimumStability, MaximumStability);
            difficulty = ClampDifficulty(InitialDifficulty(rating));
        }
        else
        {
            retrievability = Retrievability(state, reviewedAt);
            double elapsedDays = (reviewedAt - state.LastReviewAt.Value.ToUniversalTime()).TotalDays;
            stability = NextStability(state.Stability.Value, state.Difficulty.Value, retrievability, rating, elapsedDays);
            difficulty = NextDifficulty(state.Difficulty.Value, rating);
        }

        TimeSpan interval = SolveInterval(stability);
        int lapses = state.Lapses + (state.Stage == MemoryStage.Review && rating == FsrsRating.Again ? 1 : 0);
        var current = new FsrsMemoryState(
            state.CardId,
            MemoryStage.Review,
            null,
            stability,
            difficulty,
            reviewedAt.Add(interval),
            reviewedAt,
            state.Repetitions + 1,
            lapses);
        return new(state, current, rating, retrievability, interval, reviewedAt);
    }

    /// <summary>Previews all ratings from the same immutable input.</summary>
    public FsrsSchedulingPreview Preview(FsrsMemoryState state, DateTimeOffset reviewedAt) => new(
        Review(state, FsrsRating.Again, reviewedAt),
        Review(state, FsrsRating.Hard, reviewedAt),
        Review(state, FsrsRating.Good, reviewedAt),
        Review(state, FsrsRating.Easy, reviewedAt));

    /// <summary>Evaluates the eight-parameter mixed power-law forgetting curve.</summary>
    public double ForgettingCurve(double elapsedDays, double stability)
    {
        if (!double.IsFinite(elapsedDays) || elapsedDays < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedDays));
        if (!double.IsFinite(stability) || stability <= 0)
            throw new ArgumentOutOfRangeException(nameof(stability));

        double ratio = elapsedDays / stability;
        double first = PowerLaw(ratio, w[29], -w[27]);
        double second = PowerLaw(ratio, w[30], -w[28]);
        double firstWeight = w[31] * Math.Pow(stability, -w[33]);
        double secondWeight = w[32] * Math.Pow(stability, w[34]);
        return Math.Clamp(((firstWeight * first) + (secondWeight * second)) /
                          (firstWeight + secondWeight), 0, 1);
    }

    private static double PowerLaw(double elapsedOverStability, double basis, double decay)
    {
        double factor = Math.Pow(basis, 1 / decay) - 1;
        return Math.Pow(1 + (factor * elapsedOverStability), decay);
    }

    private double NextStability(
        double oldStability,
        double oldDifficulty,
        double retrievability,
        FsrsRating rating,
        double elapsedDays)
    {
        double longTerm = StabilityComponent(oldStability, oldDifficulty, retrievability, rating, 7);
        double shortTerm = StabilityComponent(oldStability, oldDifficulty, retrievability, rating, 16);
        // Zero means an entirely short-term transition; one means entirely long-term.
        double coefficient = 1 - (w[26] * Math.Exp(-w[25] * elapsedDays));
        double blended = (coefficient * longTerm) + ((1 - coefficient) * shortTerm);
        return Math.Clamp(blended, MinimumStability, MaximumStability);
    }

    private double StabilityComponent(
        double oldStability,
        double oldDifficulty,
        double retrievability,
        FsrsRating rating,
        int offset)
    {
        double failure = w[offset + 3]
            * Math.Pow(oldDifficulty, -w[offset + 4])
            * (Math.Pow(oldStability + 1, w[offset + 5]) - 1)
            * Math.Exp((1 - retrievability) * w[offset + 6]);
        double postLapse = Math.Min(oldStability, failure);
        if (rating == FsrsRating.Again)
            return postLapse;

        double ratingMultiplier = rating switch
        {
            FsrsRating.Hard => w[offset + 7],
            FsrsRating.Easy => w[offset + 8],
            _ => 1
        };
        double increase = 1
            + (Math.Exp(w[offset] - 1.5)
               * (11 - oldDifficulty)
               * Math.Pow(oldStability, -w[offset + 1])
               * (Math.Exp((1 - retrievability) * w[offset + 2]) - 1)
               * ratingMultiplier);
        return Math.Max(postLapse, oldStability * increase);
    }

    private double InitialDifficulty(FsrsRating rating) =>
        w[4] - Math.Exp(w[5] * ((int)rating - 1)) + 1;

    private double NextDifficulty(double oldDifficulty, FsrsRating rating)
    {
        double delta = -w[6] * ((int)rating - 3);
        double damped = delta * (10 - oldDifficulty) / 9;
        double reverted = (0.01 * InitialDifficulty(FsrsRating.Easy)) + (0.99 * (oldDifficulty + damped));
        return ClampDifficulty(reverted);
    }

    private TimeSpan SolveInterval(double stability)
    {
        double minimumDays = options.MinimumInterval.TotalDays;
        double maximumDays = options.MaximumInterval.TotalDays;
        if (ForgettingCurve(minimumDays, stability) <= options.DesiredRetention)
            return options.MinimumInterval;
        if (ForgettingCurve(maximumDays, stability) >= options.DesiredRetention)
            return options.MaximumInterval;

        double lower = minimumDays;
        double upper = maximumDays;
        // Bisection is deterministic and robust for this monotonic mixed curve.
        for (int iteration = 0; iteration < 80; iteration++)
        {
            double midpoint = (lower + upper) / 2;
            if (ForgettingCurve(midpoint, stability) > options.DesiredRetention)
                lower = midpoint;
            else
                upper = midpoint;
        }

        return TimeSpan.FromDays((lower + upper) / 2);
    }

    private static double ClampDifficulty(double value) => Math.Clamp(value, 1, 10);
}
