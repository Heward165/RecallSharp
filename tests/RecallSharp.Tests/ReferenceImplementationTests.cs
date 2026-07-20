using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecallSharp.Fsrs7.Experimental;
using RecallSharp.Optimization;
using RecallSharp.Policy;

namespace RecallSharp.Tests;

[TestClass]
public sealed class ReferenceImplementationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Fsrs7UsesFractionalTimeAndPreservesFsrs6Defaults()
    {
        var scheduler = new Fsrs7Scheduler();
        FsrsMemoryState state = scheduler.Review(
            FsrsMemoryState.New(1, Epoch), FsrsRating.Good, Epoch).Current;

        double immediate = scheduler.Retrievability(state, Epoch);
        double oneHour = scheduler.Retrievability(state, Epoch.AddHours(1));
        double oneDay = scheduler.Retrievability(state, Epoch.AddDays(1));

        Assert.HasCount(35, scheduler.Options.Parameters);
        Assert.HasCount(21, FsrsOptions.DefaultParameters);
        Assert.AreEqual(1d, immediate, 1e-12);
        Assert.IsTrue(oneHour < immediate && oneHour > oneDay,
            "Fractional same-day elapsed time must affect FSRS-7 retrievability.");
    }

    [TestMethod]
    public void Fsrs7TransitionsAreFiniteMonotonicAndDeterministic()
    {
        var first = new Fsrs7Scheduler();
        var second = new Fsrs7Scheduler();
        FsrsMemoryState left = FsrsMemoryState.New(9, Epoch);
        FsrsMemoryState right = left;
        DateTimeOffset now = Epoch;

        foreach (FsrsRating rating in new[] { FsrsRating.Good, FsrsRating.Hard, FsrsRating.Again, FsrsRating.Easy })
        {
            now = now.AddHours(6);
            FsrsReviewResult leftResult = first.Review(left, rating, now);
            FsrsReviewResult rightResult = second.Review(right, rating, now);
            Assert.AreEqual(leftResult, rightResult);
            Assert.IsTrue(double.IsFinite(leftResult.Current.Stability!.Value) && leftResult.Current.Stability > 0);
            Assert.IsTrue(leftResult.Current.Difficulty is >= 1 and <= 10);
            Assert.IsTrue(leftResult.Interval > TimeSpan.Zero);
            left = leftResult.Current;
            right = rightResult.Current;
        }
    }

    [TestMethod]
    public void HierarchyFallsBackUntilEvidenceThresholdIsMet()
    {
        IReadOnlyList<double> defaults = FsrsOptions.DefaultParameters;
        FsrsParameterResolution resolution = FsrsParameterHierarchy.Resolve(
            new("user", "deck", "language"),
            [
                new(FsrsParameterLevel.Application, "application", defaults, 0, Epoch),
                new(FsrsParameterLevel.User, "user", defaults, 5_000, Epoch),
                new(FsrsParameterLevel.Deck, "user/deck", defaults, 700, Epoch),
                new(FsrsParameterLevel.Category, "user/deck/language", defaults, 20, Epoch)
            ]);

        Assert.AreEqual(FsrsParameterLevel.Deck, resolution.Selected.Level);
        Assert.HasCount(1, resolution.FallbackReasons);
    }

    [TestMethod]
    public void PolicyBalancesDueDateWithoutChangingModelState()
    {
        var scheduler = new Fsrs6Scheduler(FsrsOptions.Default with
        {
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        });
        FsrsReviewResult result = scheduler.Review(FsrsMemoryState.New(4, Epoch), FsrsRating.Good, Epoch);
        DateOnly modelDay = DateOnly.FromDateTime(result.Current.DueAt.UtcDateTime);
        var loads = new Dictionary<DateOnly, int> { [modelDay] = 500 };

        FsrsPolicyDecision decision = FsrsSchedulingPolicy.Apply(
            result,
            new(Epoch, ProjectedDailyReviews: loads),
            new FsrsPolicyOptions { MaximumReviewsPerDay = 100, BalancingWindowDays = 2 });

        Assert.AreEqual(result.Current.DueAt, decision.ModelDueAt);
        Assert.IsTrue(decision.WasAdjusted);
        CollectionAssert.Contains(decision.AppliedRules.ToArray(), "workload-balancing");
    }
}
