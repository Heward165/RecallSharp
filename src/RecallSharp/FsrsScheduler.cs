namespace RecallSharp;

/// <summary>
/// Implements the published FSRS-6 Difficulty, Stability, Retrievability (DSR) model.
/// </summary>
/// <remarks>
/// This class is deterministic and has no persistence, clock, randomization, or user-interface
/// dependencies. Supply the review time explicitly, then persist the returned state in whatever
/// storage system the application chooses.
/// </remarks>
public class FsrsScheduler
{
    // FSRS prevents stability from reaching zero because it appears in denominators and powers.
    private const double MinimumStability = 0.001;
    private const double MinimumInitialStability = 0.1;
    private const double MaximumStability = 36_500;

    private readonly FsrsOptions _options;
    private readonly double[] _w;

    // In the published formula the exponent is -w[20]. Keeping it negative here
    // makes the forgetting-curve and inverse-interval equations read directly.
    private readonly double _decay;

    // Chosen so R(t = stability) is exactly 0.9 for every valid decay value.
    private readonly double _factor;

    /// <summary>Creates an FSRS-6 scheduler.</summary>
    /// <param name="options">Custom settings, or <see cref="FsrsOptions.Default"/>.</param>
    /// <exception cref="ArgumentException">The parameter set or learning steps are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Retention or maximum interval is invalid.</exception>
    public FsrsScheduler(FsrsOptions? options = null)
    {
        var configured = options ?? FsrsOptions.Default;

        ArgumentNullException.ThrowIfNull(configured.Parameters);
        ArgumentNullException.ThrowIfNull(configured.LearningSteps);
        ArgumentNullException.ThrowIfNull(configured.RelearningSteps);

        if (configured.Parameters.Count != 21)
            throw new ArgumentException("FSRS-6 requires exactly 21 parameters.", nameof(options));
        if (configured.Parameters.Any(parameter => !double.IsFinite(parameter)))
            throw new ArgumentException("FSRS parameters must be finite numbers.", nameof(options));
        if (configured.Parameters[20] <= 0)
            throw new ArgumentException("The FSRS decay parameter must be positive.", nameof(options));
        if (!double.IsFinite(configured.DesiredRetention) || configured.DesiredRetention is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Desired retention must be between 0 and 1.");
        if (configured.MaximumIntervalDays < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum interval must be positive.");
        if (configured.LearningSteps.Any(step => step <= TimeSpan.Zero) ||
            configured.RelearningSteps.Any(step => step <= TimeSpan.Zero))
            throw new ArgumentException("Learning and relearning steps must be positive.", nameof(options));

        // Copy every list so mutable collections supplied by the caller cannot alter this scheduler later.
        _w = configured.Parameters.ToArray();
        _options = configured with
        {
            Parameters = Array.AsReadOnly(_w),
            LearningSteps = Array.AsReadOnly(configured.LearningSteps.ToArray()),
            RelearningSteps = Array.AsReadOnly(configured.RelearningSteps.ToArray())
        };
        _decay = -_w[20];
        _factor = Math.Pow(0.9, 1 / _decay) - 1;
    }

    /// <summary>Gets the canonical scheduler name.</summary>
    public string Name => FsrsOptions.SchedulerName;

    /// <summary>Gets the implemented FSRS major version.</summary>
    public string Version => FsrsOptions.SchedulerVersion;

    /// <summary>Gets the target recall probability used for interval calculation.</summary>
    public double DesiredRetention => _options.DesiredRetention;

    /// <summary>Gets an immutable snapshot of this scheduler's configuration.</summary>
    public FsrsOptions Options => _options;

    /// <summary>
    /// Estimates the probability of successfully recalling a card at a specified time.
    /// </summary>
    /// <param name="state">Existing memory state.</param>
    /// <param name="at">Instant at which recall probability is requested.</param>
    /// <returns>Probability from 0 to 1, or 0 for a card that has never been reviewed.</returns>
    public double Retrievability(FsrsMemoryState state, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);

        if (state.LastReviewAt is null || state.Stability is null)
            return 0;

        // FSRS operates on completed elapsed days for long-term reviews.
        var elapsedDays = Math.Max(
            0,
            Math.Floor((at.ToUniversalTime() - state.LastReviewAt.Value.ToUniversalTime()).TotalDays));

        // FSRS-6 forgetting curve:
        // R(t, S) = (1 + factor * t / S) ^ decay
        return Math.Pow(1 + (_factor * elapsedDays / state.Stability.Value), _decay);
    }

    /// <summary>Calculates retrievability for a batch without an intermediate collection.</summary>
    /// <param name="states">States to evaluate.</param>
    /// <param name="at">Common evaluation instant.</param>
    /// <param name="destination">Destination whose length is at least the number of states.</param>
    public void Retrievabilities(
        ReadOnlySpan<FsrsMemoryState> states,
        DateTimeOffset at,
        Span<double> destination)
    {
        if (destination.Length < states.Length)
            throw new ArgumentException("The destination is shorter than the source batch.", nameof(destination));

        for (int index = 0; index < states.Length; index++)
            destination[index] = Retrievability(states[index], at);
    }

    /// <summary>
    /// Applies one rating and returns the updated memory state and next interval.
    /// The supplied state is immutable and is never modified.
    /// </summary>
    /// <param name="state">State immediately before the review.</param>
    /// <param name="rating">Learner's final self-assessment.</param>
    /// <param name="reviewedAt">UTC-capable timestamp of this review.</param>
    /// <returns>A complete description of the scheduling transition.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rating or review time is invalid.</exception>
    public FsrsReviewResult Review(FsrsMemoryState state, FsrsRating rating, DateTimeOffset reviewedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        ValidateRating(rating);

        reviewedAt = reviewedAt.ToUniversalTime();
        if (state.LastReviewAt is { } lastReview && reviewedAt < lastReview.ToUniversalTime())
            throw new ArgumentOutOfRangeException(nameof(reviewedAt), "A review cannot precede the previous review.");

        var retrievability = Retrievability(state, reviewedAt);
        var firstReview = state.Stability is null;
        var sameDay = state.LastReviewAt is not null &&
                      (reviewedAt - state.LastReviewAt.Value.ToUniversalTime()).TotalDays < 1;

        double stability;
        double difficulty;

        if (firstReview)
        {
            // A new card uses rating-specific initial values rather than the forgetting curve.
            stability = InitialStability(rating);
            difficulty = InitialDifficulty(rating);
        }
        else if (sameDay)
        {
            // FSRS-6 has a dedicated short-term stability equation for reviews less than a day apart.
            stability = ShortTermStability(state.Stability!.Value, rating);
            difficulty = NextDifficulty(state.Difficulty!.Value, rating);
        }
        else
        {
            // Long-term reviews use the recall or forgetting stability equation.
            stability = NextStability(
                state.Difficulty!.Value,
                state.Stability!.Value,
                retrievability,
                rating);
            difficulty = NextDifficulty(state.Difficulty.Value, rating);
        }

        var (stage, step, interval) = NextSchedule(state, rating, stability);

        // A lapse has the conventional Anki/FSRS meaning: a graduated Review card was forgotten.
        // Again responses while first learning a card do not increment this count.
        var lapses = state.Lapses +
                      (state.Stage == MemoryStage.Review && rating == FsrsRating.Again ? 1 : 0);

        var current = state with
        {
            Stage = stage,
            Step = step,
            Stability = stability,
            Difficulty = difficulty,
            DueAt = reviewedAt + interval,
            LastReviewAt = reviewedAt,
            Repetitions = state.Repetitions + 1,
            Lapses = lapses
        };

        return new FsrsReviewResult(state, current, rating, retrievability, interval, reviewedAt);
    }

    /// <summary>
    /// Calculates every possible rating transition at the same review instant.
    /// This is useful for presenting intervals before a learner chooses a rating.
    /// </summary>
    public FsrsSchedulingPreview Preview(FsrsMemoryState state, DateTimeOffset reviewedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(
            Review(state, FsrsRating.Again, reviewedAt),
            Review(state, FsrsRating.Hard, reviewedAt),
            Review(state, FsrsRating.Good, reviewedAt),
            Review(state, FsrsRating.Easy, reviewedAt));
    }

    /// <summary>
    /// Approximates an FSRS-6 state from the latest interval and ease factor of
    /// an SM-2-style scheduler. The conversion follows the official fsrs-rs
    /// migration equation and is intended for histories that cannot be replayed.
    /// </summary>
    /// <param name="cardId">Application-defined card identifier.</param>
    /// <param name="easeFactor">SM-2 ease multiplier, normally near 2.5.</param>
    /// <param name="intervalDays">Current SM-2 interval in days.</param>
    /// <param name="assumedRetention">Estimated retention produced by the previous scheduler.</param>
    /// <param name="lastReviewAt">Instant from which the existing interval is due.</param>
    /// <param name="repetitions">Known review count, or zero when unavailable.</param>
    /// <param name="lapses">Known lapse count, or zero when unavailable.</param>
    public FsrsMemoryState MigrateFromSm2(
        long cardId,
        double easeFactor,
        double intervalDays,
        double assumedRetention,
        DateTimeOffset lastReviewAt,
        int repetitions = 0,
        int lapses = 0)
    {
        if (!double.IsFinite(easeFactor) || easeFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(easeFactor));
        if (!double.IsFinite(intervalDays) || intervalDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalDays));
        if (!double.IsFinite(assumedRetention) || assumedRetention is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(assumedRetention));
        if (lastReviewAt == default)
            throw new ArgumentOutOfRangeException(nameof(lastReviewAt));
        if (repetitions < 0)
            throw new ArgumentOutOfRangeException(nameof(repetitions));
        if (lapses < 0 || lapses > repetitions)
            throw new ArgumentOutOfRangeException(nameof(lapses));

        // Invert the FSRS forgetting curve at the previous scheduler's retention.
        var stability = intervalDays * _factor /
                        (Math.Pow(assumedRetention, 1 / _decay) - 1);

        // Infer difficulty from the desired SM-2 interval multiplier. This is the
        // same approximation used by the reference fsrs-rs implementation.
        var difficulty = 11 - ((easeFactor - 1) /
            (Math.Exp(_w[8]) * Math.Pow(stability, -_w[9]) *
             (Math.Exp((1 - assumedRetention) * _w[10]) - 1)));

        if (!double.IsFinite(stability) || !double.IsFinite(difficulty))
            throw new ArgumentException("The SM-2 values do not produce a finite FSRS state.");

        lastReviewAt = lastReviewAt.ToUniversalTime();
        return new(
            cardId,
            MemoryStage.Review,
            null,
            ClampStability(stability),
            ClampDifficulty(difficulty),
            lastReviewAt.AddDays(intervalDays),
            lastReviewAt,
            repetitions,
            lapses);
    }

    /// <summary>
    /// Converts a stability value into a whole-day interval at the configured desired retention.
    /// </summary>
    /// <param name="stability">Memory stability measured in days.</param>
    /// <returns>A rounded interval clamped to 1 through <see cref="FsrsOptions.MaximumIntervalDays"/>.</returns>
    public int IntervalDays(double stability)
    {
        if (!double.IsFinite(stability) || stability <= 0)
            throw new ArgumentOutOfRangeException(nameof(stability), "Stability must be a positive finite number.");

        // This is the inverse of the forgetting curve with R replaced by desired retention.
        var interval = (stability / _factor) *
                       (Math.Pow(_options.DesiredRetention, 1 / _decay) - 1);

        return Math.Clamp((int)Math.Round(interval), 1, _options.MaximumIntervalDays);
    }

    private (MemoryStage Stage, int? Step, TimeSpan Interval) NextSchedule(
        FsrsMemoryState previous,
        FsrsRating rating,
        double stability)
    {
        // FSRS determines memory strength; this switch combines it with optional sub-day steps.
        return previous.Stage switch
        {
            MemoryStage.New or MemoryStage.Learning => ScheduleSteps(
                rating,
                previous.Stage == MemoryStage.New ? 0 : previous.Step ?? 0,
                _options.LearningSteps,
                stability,
                MemoryStage.Learning),

            MemoryStage.Review when rating == FsrsRating.Again && _options.RelearningSteps.Count > 0 =>
                (MemoryStage.Relearning, 0, _options.RelearningSteps[0]),

            MemoryStage.Review =>
                (MemoryStage.Review, null, TimeSpan.FromDays(IntervalDays(stability))),

            MemoryStage.Relearning => ScheduleSteps(
                rating,
                previous.Step ?? 0,
                _options.RelearningSteps,
                stability,
                MemoryStage.Relearning),

            _ => throw new ArgumentOutOfRangeException(nameof(previous))
        };
    }

    private (MemoryStage Stage, int? Step, TimeSpan Interval) ScheduleSteps(
        FsrsRating rating,
        int step,
        IReadOnlyList<TimeSpan> steps,
        double stability,
        MemoryStage activeStage)
    {
        // With no configured steps, every successful rating immediately enters Review.
        if (steps.Count == 0 || (step >= steps.Count && rating != FsrsRating.Again))
            return Graduate(stability);

        return rating switch
        {
            // Again restarts the short learning sequence.
            FsrsRating.Again => (activeStage, 0, steps[0]),

            // Hard is a successful recall, but it repeats the current short step.
            FsrsRating.Hard =>
                (activeStage, Math.Min(step, steps.Count - 1), HardStep(step, steps)),

            // Good advances one step or graduates after the final step.
            FsrsRating.Good when step + 1 >= steps.Count => Graduate(stability),
            FsrsRating.Good => (activeStage, step + 1, steps[step + 1]),

            // Easy always graduates immediately.
            FsrsRating.Easy => Graduate(stability),
            _ => throw new ArgumentOutOfRangeException(nameof(rating))
        };
    }

    private (MemoryStage Stage, int? Step, TimeSpan Interval) Graduate(double stability) =>
        (MemoryStage.Review, null, TimeSpan.FromDays(IntervalDays(stability)));

    private static TimeSpan HardStep(int step, IReadOnlyList<TimeSpan> steps)
    {
        // This follows the conventional FSRS learning-step treatment:
        // one step -> 1.5x that step; first of many -> midpoint of the first two.
        if (step == 0 && steps.Count == 1)
            return TimeSpan.FromTicks((long)(steps[0].Ticks * 1.5));
        if (step == 0)
            return TimeSpan.FromTicks((steps[0].Ticks + steps[1].Ticks) / 2);
        return steps[Math.Min(step, steps.Count - 1)];
    }

    private double InitialStability(FsrsRating rating)
    {
        // S0(G) = w[G - 1]
        return Math.Clamp(_w[(int)rating - 1], MinimumInitialStability, MaximumStability);
    }

    private double InitialDifficulty(FsrsRating rating)
    {
        // D0(G) = w[4] - exp(w[5] * (G - 1)) + 1
        return ClampDifficulty(RawInitialDifficulty(rating));
    }

    private double RawInitialDifficulty(FsrsRating rating) =>
        _w[4] - Math.Exp(_w[5] * ((int)rating - 1)) + 1;

    private double ShortTermStability(double stability, FsrsRating rating)
    {
        // S'(S,G) = S * exp(w[17] * (G - 3 + w[18])) * S^(-w[19])
        var increase = Math.Exp(_w[17] * ((int)rating - 3 + _w[18])) *
                       Math.Pow(stability, -_w[19]);

        // Successful same-day recall must not make memory less stable.
        if (rating is FsrsRating.Hard or FsrsRating.Good or FsrsRating.Easy)
            increase = Math.Max(increase, 1);

        return ClampStability(stability * increase);
    }

    private double NextDifficulty(double difficulty, FsrsRating rating)
    {
        // First apply the rating delta with linear damping near difficulty 10.
        var delta = -_w[6] * ((int)rating - 3);
        var dampedDelta = (10 - difficulty) * delta / 9;
        var difficultyAfterRating = difficulty + dampedDelta;

        // Then apply mean reversion toward the initial Easy difficulty to avoid "ease hell".
        var meanReverted = (_w[7] * RawInitialDifficulty(FsrsRating.Easy)) +
                           ((1 - _w[7]) * difficultyAfterRating);
        return ClampDifficulty(meanReverted);
    }

    private double NextStability(
        double difficulty,
        double stability,
        double retrievability,
        FsrsRating rating) =>
        ClampStability(rating == FsrsRating.Again
            ? ForgetStability(difficulty, stability, retrievability)
            : RecallStability(difficulty, stability, retrievability, rating));

    private double ForgetStability(double difficulty, double stability, double retrievability)
    {
        // Long-term post-lapse stability from difficulty, previous stability, and recall probability.
        var longTerm = _w[11]
                       * Math.Pow(difficulty, -_w[12])
                       * (Math.Pow(stability + 1, _w[13]) - 1)
                       * Math.Exp(_w[14] * (1 - retrievability));

        // FSRS-6 caps the long-term value using the short-term forgetting model.
        var shortTermLimit = stability / Math.Exp(_w[17] * _w[18]);
        return Math.Min(longTerm, shortTermLimit);
    }

    private double RecallStability(
        double difficulty,
        double stability,
        double retrievability,
        FsrsRating rating)
    {
        // Hard and Easy change the successful-recall gain without changing pass/fail semantics.
        var hardPenalty = rating == FsrsRating.Hard ? _w[15] : 1;
        var easyBonus = rating == FsrsRating.Easy ? _w[16] : 1;

        // Successful-recall stability equation. The gain grows with spacing (lower R),
        // and shrinks for more difficult or already-stable memories.
        return stability * (1
            + Math.Exp(_w[8])
            * (11 - difficulty)
            * Math.Pow(stability, -_w[9])
            * (Math.Exp((1 - retrievability) * _w[10]) - 1)
            * hardPenalty
            * easyBonus);
    }

    private static double ClampDifficulty(double value) => Math.Clamp(value, 1, 10);

    private static double ClampStability(double value) =>
        Math.Clamp(value, MinimumStability, MaximumStability);

    private static void ValidateRating(FsrsRating rating)
    {
        if (rating is < FsrsRating.Again or > FsrsRating.Easy)
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be Again, Hard, Good, or Easy.");
    }

    private static void ValidateState(FsrsMemoryState state)
    {
        // Stability and difficulty are both absent before the first review and both present afterward.
        if (state.Stability.HasValue != state.Difficulty.HasValue)
            throw new ArgumentException("Stability and difficulty must either both be set or both be null.", nameof(state));
        if (state.Stability is { } stability && (!double.IsFinite(stability) || stability <= 0))
            throw new ArgumentException("Stability must be a positive finite number.", nameof(state));
        if (state.Difficulty is { } difficulty && (!double.IsFinite(difficulty) || difficulty is < 1 or > 10))
            throw new ArgumentException("Difficulty must be a finite number from 1 to 10.", nameof(state));
        if (state.Step < 0)
            throw new ArgumentException("A learning step cannot be negative.", nameof(state));
        if (state.Repetitions < 0 || state.Lapses < 0)
            throw new ArgumentException("Repetition and lapse counts cannot be negative.", nameof(state));
    }
}
