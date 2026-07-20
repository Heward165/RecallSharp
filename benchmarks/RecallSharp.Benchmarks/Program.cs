using System.Diagnostics;
using System.Text.Json;
using RecallSharp;

const int iterations = 250_000;
var scheduler = new Fsrs6Scheduler(FsrsOptions.Default with
{
    LearningSteps = Array.Empty<TimeSpan>(),
    RelearningSteps = Array.Empty<TimeSpan>()
});
DateTimeOffset time = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
FsrsMemoryState state = FsrsMemoryState.New(1, time);

// Warm JIT-generated code before measuring steady-state work.
for (int index = 0; index < 1_000; index++)
{
    time = time.AddDays(1);
    state = scheduler.Review(state, FsrsRating.Good, time).Current;
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var stopwatch = Stopwatch.StartNew();
for (int index = 0; index < iterations; index++)
{
    time = time.AddDays(1);
    FsrsRating rating = index % 17 == 0 ? FsrsRating.Again : FsrsRating.Good;
    state = scheduler.Review(state, rating, time).Current;
}
stopwatch.Stop();
long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

var report = new
{
    iterations,
    elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
    reviewsPerSecond = iterations / stopwatch.Elapsed.TotalSeconds,
    allocatedBytesPerReview = allocated / (double)iterations,
    runtime = Environment.Version.ToString(),
    operatingSystem = Environment.OSVersion.ToString()
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
