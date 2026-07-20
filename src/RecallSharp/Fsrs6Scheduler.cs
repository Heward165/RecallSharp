namespace RecallSharp;

/// <summary>
/// Version-explicit name for the FSRS-6 scheduler. <see cref="FsrsScheduler"/>
/// remains available for source compatibility.
/// </summary>
public sealed class Fsrs6Scheduler : FsrsScheduler
{
    /// <summary>Creates an FSRS-6 scheduler with optional custom parameters.</summary>
    public Fsrs6Scheduler(FsrsOptions? options = null)
        : base(options)
    {
    }
}
