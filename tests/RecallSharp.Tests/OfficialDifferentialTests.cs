using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RecallSharp.Tests;

[TestClass]
public sealed class OfficialDifferentialTests
{
    [TestMethod]
    public void Fsrs6TransitionsMatchOfficialTsFsrsAcross2048Vectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "ts-fsrs-5.4.1-vectors.json");
        ReferenceDocument document = JsonSerializer.Deserialize<ReferenceDocument>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.AreEqual("open-spaced-repetition/ts-fsrs", document.Source);
        Assert.AreEqual("FSRS-6.0", document.Algorithm);
        Assert.HasCount(2048, document.Vectors);

        var scheduler = new Fsrs6Scheduler(FsrsOptions.Default with
        {
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        });
        DateTimeOffset epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        foreach (ReferenceVector vector in document.Vectors)
        {
            var state = new FsrsMemoryState(
                1,
                MemoryStage.Review,
                null,
                vector.Stability,
                vector.Difficulty,
                epoch,
                epoch,
                1,
                0);
            FsrsMemoryState actual = scheduler.Review(
                state,
                (FsrsRating)vector.Rating,
                epoch.AddDays(vector.ElapsedDays)).Current;

            double stabilityTolerance = Math.Max(1e-7, Math.Abs(vector.ExpectedStability) * 2e-7);
            Assert.AreEqual(vector.ExpectedStability, actual.Stability!.Value, stabilityTolerance);
            Assert.AreEqual(vector.ExpectedDifficulty, actual.Difficulty!.Value, 2e-7);
        }
    }

    private sealed record ReferenceDocument(
        string Source,
        string PackageVersion,
        string Algorithm,
        string GeneratedAt,
        ReferenceVector[] Vectors);

    private sealed record ReferenceVector(
        double Stability,
        double Difficulty,
        int ElapsedDays,
        int Rating,
        double ExpectedStability,
        double ExpectedDifficulty);
}
