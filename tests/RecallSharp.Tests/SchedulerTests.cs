using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecallSharp.Optimizer;

namespace RecallSharp.Tests;

[TestClass]
public sealed class SchedulerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PreviewMatchesFourIndependentTransitions()
    {
        var scheduler = new Fsrs6Scheduler();
        FsrsMemoryState state = FsrsMemoryState.New(7, Epoch);
        FsrsSchedulingPreview preview = scheduler.Preview(state, Epoch);

        foreach (FsrsRating rating in Enum.GetValues<FsrsRating>())
            Assert.AreEqual(scheduler.Review(state, rating, Epoch), preview[rating]);

        Assert.AreEqual(MemoryStage.New, state.Stage, "Preview must not mutate its input.");
    }

    [TestMethod]
    [DataRow(2.5, 10.0, 0.9, 10.0, 6.9140563)]
    [DataRow(2.5, 10.0, 0.8, 3.01572, 9.393428)]
    [DataRow(2.5, 10.0, 0.95, 24.841097, 1.2974405)]
    [DataRow(1.3, 20.0, 0.9, 20.0, 10.0)]
    public void Sm2MigrationMatchesOfficialFsrsRsVectors(
        double ease,
        double interval,
        double retention,
        double expectedStability,
        double expectedDifficulty)
    {
        FsrsMemoryState state = new Fsrs6Scheduler().MigrateFromSm2(
            1, ease, interval, retention, Epoch, repetitions: 5, lapses: 1);

        Assert.AreEqual(expectedStability, state.Stability!.Value, 1e-5);
        Assert.AreEqual(expectedDifficulty, state.Difficulty!.Value, 1e-5);
        Assert.AreEqual(Epoch.AddDays(interval), state.DueAt);
        Assert.AreEqual(MemoryStage.Review, state.Stage);
    }

    [TestMethod]
    public void TenThousandRandomizedTransitionsPreserveStateInvariants()
    {
        var scheduler = new Fsrs6Scheduler(FsrsOptions.Default with
        {
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        });
        var random = new Random(20260717);
        DateTimeOffset time = Epoch;
        FsrsMemoryState state = FsrsMemoryState.New(99, time);

        for (int index = 0; index < 10_000; index++)
        {
            time = time.AddMinutes(random.Next(1, 60 * 24 * 30));
            FsrsRating rating = (FsrsRating)random.Next(1, 5);
            FsrsReviewResult result = scheduler.Review(state, rating, time);
            state = result.Current;

            Assert.IsTrue(double.IsFinite(state.Stability!.Value) && state.Stability > 0);
            Assert.IsTrue(double.IsFinite(state.Difficulty!.Value) && state.Difficulty is >= 1 and <= 10);
            Assert.IsTrue(result.PredictedRetrievability is >= 0 and <= 1);
            Assert.IsTrue(state.DueAt > time);
            Assert.AreEqual(index + 1, state.Repetitions);
            Assert.IsLessThanOrEqualTo(state.Repetitions, state.Lapses);
        }
    }

    [TestMethod]
    public void OptimizerIsDeterministicAndNeverReturnsWorseParameters()
    {
        IReadOnlyList<FsrsTrainingItem> data = SyntheticHistory();
        var optimizer = new FsrsParameterOptimizer(new(
            MaximumPasses: 2,
            InitialRelativeStep: 0.04,
            MinimumRelativeStep: 0.01,
            MinimumPredictiveReviews: 20));

        FsrsOptimizationResult first = optimizer.Optimize(data);
        FsrsOptimizationResult second = optimizer.Optimize(data);

        CollectionAssert.AreEqual(first.Parameters.ToArray(), second.Parameters.ToArray());
        Assert.IsLessThanOrEqualTo(first.BaselineLogLoss, first.OptimizedLogLoss);
        Assert.IsGreaterThanOrEqualTo(20, first.PredictiveReviewCount);
    }

    private static IReadOnlyList<FsrsTrainingItem> SyntheticHistory()
    {
        var items = new List<FsrsTrainingItem>();
        for (int card = 0; card < 8; card++)
        {
            var reviews = new List<FsrsReviewObservation>();
            DateTimeOffset time = Epoch.AddMinutes(card);
            for (int review = 0; review < 8; review++)
            {
                time = time.AddDays(1 + card + review);
                FsrsRating rating = (review + card) % 5 == 0 ? FsrsRating.Again : FsrsRating.Good;
                reviews.Add(new(time, rating));
            }

            items.Add(new(card, reviews));
        }

        return items;
    }
}
