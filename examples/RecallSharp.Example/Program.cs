using RecallSharp;

DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
var scheduler = new Fsrs6Scheduler();
FsrsMemoryState card = FsrsMemoryState.New(42, now);
FsrsSchedulingPreview preview = scheduler.Preview(card, now);

foreach (FsrsRating rating in Enum.GetValues<FsrsRating>())
{
    FsrsReviewResult choice = preview[rating];
    Console.WriteLine($"{rating,-5} {choice.Interval:g} due {choice.Current.DueAt:O}");
}

FsrsMemoryState migrated = scheduler.MigrateFromSm2(
    cardId: 100,
    easeFactor: 2.5,
    intervalDays: 10,
    assumedRetention: 0.9,
    lastReviewAt: now);
Console.WriteLine($"Migrated: S={migrated.Stability:F4}, D={migrated.Difficulty:F4}");
