using System.Collections.ObjectModel;

namespace RecallSharp.Fsrs7.Experimental;

/// <summary>
/// Configures the experimental FSRS-7 equations. The API is intentionally isolated from
/// <see cref="global::RecallSharp.FsrsOptions"/> so FSRS-6 behavior cannot change silently.
/// </summary>
public sealed record Fsrs7Options
{
    /// <summary>Target probability used when numerically solving the next interval.</summary>
    public double DesiredRetention { get; init; } = 0.90;

    /// <summary>Smallest interval emitted by the model.</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Largest interval emitted by the model.</summary>
    public TimeSpan MaximumInterval { get; init; } = TimeSpan.FromDays(36_500);

    /// <summary>FSRS-7's ordered 35 parameters.</summary>
    public IReadOnlyList<double> Parameters { get; init; } = DefaultParameters;

    /// <summary>
    /// Population defaults published by the Open Spaced Repetition SRS benchmark.
    /// These defaults are experimental and versioned independently from RecallSharp FSRS-6.
    /// </summary>
    public static IReadOnlyList<double> DefaultParameters { get; } =
        new ReadOnlyCollection<double>(
        [
            0.041, 2.4175, 4.1283, 11.9709,
            5.6385, 0.4468, 3.262,
            2.3054, 0.1688, 1.3325, 0.3524, 0.0049, 0.7503, 0.0896, 0.6625, 1.3,
            0.882, 0.3072, 3.5875, 0.303, 0.0107, 0.2279, 2.6413, 0.5594, 1.3,
            2.5, 1.0,
            0.0723, 0.1634, 0.5, 0.9555, 0.2245, 0.6232, 0.1362, 0.3862
        ]);

    /// <summary>Balanced experimental defaults.</summary>
    public static Fsrs7Options Default { get; } = new();
}
