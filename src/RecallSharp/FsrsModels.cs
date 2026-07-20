using System.Collections.ObjectModel;

namespace RecallSharp;

/// <summary>
/// Describes how successfully the learner recalled a card.
/// The numeric values are part of the FSRS specification and must remain 1 through 4.
/// </summary>
public enum FsrsRating
{
    /// <summary>The answer was forgotten, incorrect, or materially incomplete.</summary>
    Again = 1,

    /// <summary>The answer was correct but required substantial effort or hesitation.</summary>
    Hard = 2,

    /// <summary>The answer was correct with ordinary effort.</summary>
    Good = 3,

    /// <summary>The answer was correct, immediate, and unambiguous.</summary>
    Easy = 4
}

/// <summary>
/// Identifies the scheduling phase of a card.
/// </summary>
public enum MemoryStage
{
    /// <summary>The card has never been reviewed.</summary>
    New,

    /// <summary>The card is moving through short initial learning steps.</summary>
    Learning,

    /// <summary>The card has graduated to day-based FSRS scheduling.</summary>
    Review,

    /// <summary>A graduated card was forgotten and is moving through relearning steps.</summary>
    Relearning
}

/// <summary>
/// Contains all mutable scheduling data for one card.
/// Content such as prompts and answers deliberately does not belong in this model.
/// </summary>
/// <param name="CardId">Application-defined identifier for the card.</param>
/// <param name="Stage">Current learning phase.</param>
/// <param name="Step">Zero-based learning/relearning step, or <see langword="null"/> in review.</param>
/// <param name="Stability">Days until retrievability falls to 90%, or <see langword="null"/> before the first review.</param>
/// <param name="Difficulty">Estimated difficulty from 1 (easy) to 10 (hard), or <see langword="null"/> before the first review.</param>
/// <param name="DueAt">UTC instant at which the card becomes due.</param>
/// <param name="LastReviewAt">UTC instant of the previous review.</param>
/// <param name="Repetitions">Total number of completed reviews.</param>
/// <param name="Lapses">Number of times a graduated review card was rated Again.</param>
public sealed record FsrsMemoryState(
    long CardId,
    MemoryStage Stage,
    int? Step,
    double? Stability,
    double? Difficulty,
    DateTimeOffset DueAt,
    DateTimeOffset? LastReviewAt,
    int Repetitions,
    int Lapses)
{
    /// <summary>Creates the initial scheduling state for a card that is immediately available.</summary>
    /// <param name="cardId">Application-defined identifier.</param>
    /// <param name="createdAt">Creation instant; this is also the initial due time.</param>
    public static FsrsMemoryState New(long cardId, DateTimeOffset createdAt) =>
        new(cardId, MemoryStage.New, 0, null, null, createdAt.ToUniversalTime(), null, 0, 0);
}

/// <summary>
/// Describes one pure FSRS transition. The caller is responsible for persisting
/// <see cref="Current"/> and any desired review history.
/// </summary>
/// <param name="Previous">State supplied to the scheduler.</param>
/// <param name="Current">Resulting state after the rating.</param>
/// <param name="Rating">Rating used for the transition.</param>
/// <param name="PredictedRetrievability">Recall probability immediately before the review.</param>
/// <param name="Interval">Time between the review and the next due instant.</param>
/// <param name="ReviewedAt">UTC review instant.</param>
public sealed record FsrsReviewResult(
    FsrsMemoryState Previous,
    FsrsMemoryState Current,
    FsrsRating Rating,
    double PredictedRetrievability,
    TimeSpan Interval,
    DateTimeOffset ReviewedAt);

/// <summary>
/// Shows the four schedules a learner would receive at a review instant without
/// mutating or choosing a rating for the supplied state.
/// </summary>
/// <param name="Again">Transition produced by <see cref="FsrsRating.Again"/>.</param>
/// <param name="Hard">Transition produced by <see cref="FsrsRating.Hard"/>.</param>
/// <param name="Good">Transition produced by <see cref="FsrsRating.Good"/>.</param>
/// <param name="Easy">Transition produced by <see cref="FsrsRating.Easy"/>.</param>
public sealed record FsrsSchedulingPreview(
    FsrsReviewResult Again,
    FsrsReviewResult Hard,
    FsrsReviewResult Good,
    FsrsReviewResult Easy)
{
    /// <summary>Returns the preview associated with a rating.</summary>
    public FsrsReviewResult this[FsrsRating rating] => rating switch
    {
        FsrsRating.Again => Again,
        FsrsRating.Hard => Hard,
        FsrsRating.Good => Good,
        FsrsRating.Easy => Easy,
        _ => throw new ArgumentOutOfRangeException(nameof(rating))
    };
}

/// <summary>
/// Configures the FSRS-6 equations and the short learning steps surrounding them.
/// </summary>
/// <param name="DesiredRetention">Target recall probability used to calculate intervals.</param>
/// <param name="LearningSteps">Sub-day delays used before a new card graduates.</param>
/// <param name="RelearningSteps">Sub-day delays used after a review card is forgotten.</param>
/// <param name="MaximumIntervalDays">Upper bound for a day-based interval.</param>
/// <param name="Parameters">The 21 ordered FSRS-6 weights.</param>
public sealed record FsrsOptions(
    double DesiredRetention,
    IReadOnlyList<TimeSpan> LearningSteps,
    IReadOnlyList<TimeSpan> RelearningSteps,
    int MaximumIntervalDays,
    IReadOnlyList<double> Parameters)
{
    /// <summary>Canonical scheduler name stored with review history.</summary>
    public const string SchedulerName = "FSRS";

    /// <summary>Implemented major algorithm version.</summary>
    public const string SchedulerVersion = "6";

    /// <summary>
    /// Default FSRS-6 weights published by the Open Spaced Repetition project.
    /// The collection is read-only so callers cannot alter global defaults.
    /// </summary>
    public static IReadOnlyList<double> DefaultParameters { get; } =
        new ReadOnlyCollection<double>(
        [
            0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194,
            0.001, 1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629,
            1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658, 0.1542
        ]);

    /// <summary>
    /// Balanced defaults: 90% desired retention, one 10-minute learning step,
    /// one 10-minute relearning step, and a 100-year maximum interval.
    /// </summary>
    public static FsrsOptions Default { get; } = new(
        DesiredRetention: 0.90,
        LearningSteps: new ReadOnlyCollection<TimeSpan>([TimeSpan.FromMinutes(10)]),
        RelearningSteps: new ReadOnlyCollection<TimeSpan>([TimeSpan.FromMinutes(10)]),
        MaximumIntervalDays: 36_500,
        Parameters: DefaultParameters);
}
