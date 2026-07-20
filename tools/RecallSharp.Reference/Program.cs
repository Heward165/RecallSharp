using System.Globalization;
using System.Text;
using System.Text.Json;
using RecallSharp;
using RecallSharp.Analytics;
using RecallSharp.Fsrs7.Experimental;
using RecallSharp.Optimization;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: RecallSharp.Reference <reviews.csv> [output-directory]");
    Console.Error.WriteLine("CSV: user_id,card_id,reviewed_at,rating");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputDirectory = args.Length == 2
    ? Path.GetFullPath(args[1])
    : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "reference-run"));
IReadOnlyList<BenchmarkReview> reviews = BenchmarkCsv.Import(File.ReadAllText(inputPath));
ReferenceRun report = ReferencePipeline.Run(reviews);
Directory.CreateDirectory(outputDirectory);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(outputDirectory, "summary.json"), JsonSerializer.Serialize(report.Summary, jsonOptions));
WriteRaw(Path.Combine(outputDirectory, "fsrs6.raw.jsonl"), report.Fsrs6Raw);
WriteRaw(Path.Combine(outputDirectory, "fsrs7.raw.jsonl"), report.Fsrs7Raw);
WriteRaw(Path.Combine(outputDirectory, "baseline.raw.jsonl"), report.BaselineRaw);

Console.WriteLine($"Users: {report.Summary.UserCount}; held-out reviews: {report.Summary.Fsrs6.ObservationCount}");
Console.WriteLine($"FSRS-6 log loss: {report.Summary.Fsrs6.LogLoss:F6}");
Console.WriteLine($"FSRS-7 log loss: {report.Summary.Fsrs7.LogLoss:F6}");
Console.WriteLine($"Baseline log loss: {report.Summary.Baseline.LogLoss:F6}");
Console.WriteLine($"Suggested retention: {report.Summary.Workload.Best.DesiredRetention:P0}");
Console.WriteLine($"Artifacts: {outputDirectory}");
return 0;

static void WriteRaw(string path, IReadOnlyList<RawPrediction> records)
{
    using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
    foreach (RawPrediction record in records)
        writer.WriteLine(JsonSerializer.Serialize(record));
}

internal sealed record BenchmarkReview(
    long UserId,
    long CardId,
    DateTimeOffset ReviewedAt,
    FsrsRating Rating);

internal static class BenchmarkCsv
{
    private const string Header = "user_id,card_id,reviewed_at,rating";

    public static IReadOnlyList<BenchmarkReview> Import(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Expected CSV header '{Header}'.");

        var reviews = new List<BenchmarkReview>();
        for (int index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                continue;
            string[] fields = lines[index].Split(',');
            if (fields.Length != 4 ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long cardId) ||
                !DateTimeOffset.TryParseExact(fields[2], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset reviewedAt) ||
                !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rating) ||
                rating is < 1 or > 4)
                throw new FormatException($"Invalid benchmark row {index + 1}.");
            reviews.Add(new(userId, cardId, reviewedAt.ToUniversalTime(), (FsrsRating)rating));
        }

        if (reviews.Count == 0)
            throw new FormatException("The benchmark contains no reviews.");
        return reviews;
    }
}

internal sealed record PredictionSample(
    long UserId,
    double Prediction,
    int Actual,
    double ElapsedDays,
    int ReviewNumber,
    int Lapses);

internal sealed record BenchmarkMetrics(
    int ObservationCount,
    double LogLoss,
    double RootMeanSquaredError,
    double RmseBins,
    double AreaUnderCurve,
    double ExpectedCalibrationError);

internal sealed record RawPrediction(long user, IReadOnlyList<double> p, IReadOnlyList<int> y);

internal sealed record OptimizationSummary(
    int TrainingCards,
    int ValidationCards,
    double TrainingLogLoss,
    double ValidationLogLoss,
    IReadOnlyList<string> Warnings);

internal sealed record ReferenceSummary(
    int UserCount,
    BenchmarkMetrics Fsrs6,
    BenchmarkMetrics Fsrs7,
    BenchmarkMetrics Baseline,
    OptimizationSummary? Optimization,
    OptimalRetentionReport Workload);

internal sealed record ReferenceRun(
    ReferenceSummary Summary,
    IReadOnlyList<RawPrediction> Fsrs6Raw,
    IReadOnlyList<RawPrediction> Fsrs7Raw,
    IReadOnlyList<RawPrediction> BaselineRaw);

internal static class ReferencePipeline
{
    public static ReferenceRun Run(IReadOnlyList<BenchmarkReview> reviews)
    {
        var fsrs6Samples = new List<PredictionSample>();
        var fsrs7Samples = new List<PredictionSample>();
        var baselineSamples = new List<PredictionSample>();
        var fsrs6 = new Fsrs6Scheduler(FsrsOptions.Default with
        {
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        });
        var fsrs7 = new Fsrs7Scheduler();

        foreach (IGrouping<long, BenchmarkReview> user in reviews.GroupBy(review => review.UserId).OrderBy(group => group.Key))
        {
            BenchmarkReview[] ordered = user.OrderBy(review => review.ReviewedAt).ThenBy(review => review.CardId).ToArray();
            DateTimeOffset cutoff = ordered[Math.Clamp((int)Math.Floor(ordered.Length * 0.8), 1, ordered.Length - 1)].ReviewedAt;
            BenchmarkReview[] predictiveTraining = ordered
                .GroupBy(review => review.CardId)
                .SelectMany(card => card.OrderBy(review => review.ReviewedAt).Skip(1))
                .Where(review => review.ReviewedAt < cutoff)
                .ToArray();
            double baseline = Math.Clamp(
                predictiveTraining.Length == 0 ? 0.9 : predictiveTraining.Average(review => review.Rating == FsrsRating.Again ? 0 : 1),
                1e-7,
                1 - 1e-7);

            foreach (IGrouping<long, BenchmarkReview> card in ordered.GroupBy(review => review.CardId))
            {
                BenchmarkReview[] history = card.OrderBy(review => review.ReviewedAt).ToArray();
                FsrsMemoryState state6 = FsrsMemoryState.New(card.Key, history[0].ReviewedAt);
                FsrsMemoryState state7 = FsrsMemoryState.New(card.Key, history[0].ReviewedAt);
                int lapses = 0;
                for (int index = 0; index < history.Length; index++)
                {
                    BenchmarkReview review = history[index];
                    if (index > 0 && review.ReviewedAt >= cutoff)
                    {
                        double elapsed = (review.ReviewedAt - history[index - 1].ReviewedAt).TotalDays;
                        int actual = review.Rating == FsrsRating.Again ? 0 : 1;
                        fsrs6Samples.Add(new(user.Key, Clamp(fsrs6.Retrievability(state6, review.ReviewedAt)), actual, elapsed, index + 1, lapses));
                        fsrs7Samples.Add(new(user.Key, Clamp(fsrs7.Retrievability(state7, review.ReviewedAt)), actual, elapsed, index + 1, lapses));
                        baselineSamples.Add(new(user.Key, baseline, actual, elapsed, index + 1, lapses));
                    }

                    state6 = fsrs6.Review(state6, review.Rating, review.ReviewedAt).Current;
                    state7 = fsrs7.Review(state7, review.Rating, review.ReviewedAt).Current;
                    if (index > 0 && review.Rating == FsrsRating.Again)
                        lapses++;
                }
            }
        }

        if (fsrs6Samples.Count == 0)
            throw new InvalidOperationException("At least one held-out predictive review is required.");

        FsrsOptimizationResult? optimized = Optimize(reviews);
        IReadOnlyList<double> workloadParameters = optimized?.Parameters ?? FsrsOptions.DefaultParameters;
        OptimalRetentionReport workload = OptimalRetentionCalculator.Evaluate(
            new DeckSimulationDefinition { Days = 365, DeckSize = 1_000, NewCardsPerDay = 10, Seed = 42 },
            [0.80, 0.85, 0.90, 0.92, 0.95],
            new RetentionObjective(0.25, 1, 500),
            workloadParameters);
        OptimizationSummary? optimization = optimized is null ? null : new(
            optimized.TrainingCardCount,
            optimized.ValidationCardCount,
            optimized.TrainingMetrics.LogLoss,
            optimized.ValidationMetrics.LogLoss,
            optimized.Warnings);
        return new(
            new(reviews.Select(review => review.UserId).Distinct().Count(),
                Metrics(fsrs6Samples), Metrics(fsrs7Samples), Metrics(baselineSamples), optimization, workload),
            Raw(fsrs6Samples), Raw(fsrs7Samples), Raw(baselineSamples));
    }

    private static FsrsOptimizationResult? Optimize(IReadOnlyList<BenchmarkReview> reviews)
    {
        var logs = new List<FsrsReviewLog>();
        long mappedCardId = 0;
        foreach (IGrouping<(long UserId, long CardId), BenchmarkReview> card in reviews
                     .GroupBy(review => (review.UserId, review.CardId))
                     .OrderBy(group => group.Key.UserId)
                     .ThenBy(group => group.Key.CardId))
        {
            mappedCardId++;
            logs.AddRange(card.Select(review => new FsrsReviewLog(mappedCardId, review.ReviewedAt, review.Rating)));
        }

        IReadOnlyList<FsrsCardHistory> histories = FsrsCardHistory.Group(logs)
            .Where(history => history.Reviews.Count >= 2)
            .ToArray();
        if (histories.Count < 2)
            return null;
        return FsrsParameterOptimizer.Optimize(histories, new FsrsOptimizationOptions
        {
            MaximumPasses = 3,
            InitialLogStep = 0.10,
            MinimumLogStep = 0.01
        });
    }

    private static BenchmarkMetrics Metrics(IReadOnlyList<PredictionSample> samples)
    {
        double logLoss = samples.Average(sample => -(sample.Actual * Math.Log(sample.Prediction) +
            ((1 - sample.Actual) * Math.Log(1 - sample.Prediction))));
        double rmse = Math.Sqrt(samples.Average(sample => Math.Pow(sample.Prediction - sample.Actual, 2)));
        double rmseBins = Math.Sqrt(samples
            .GroupBy(sample => (
                Interval: (int)Math.Floor(Math.Log2(Math.Max(sample.ElapsedDays * 24, 1d / 60))),
                Review: Math.Min(sample.ReviewNumber, 10),
                Lapses: Math.Min(sample.Lapses, 5)))
            .Select(group => new { Count = group.Count(), Error = Math.Pow(group.Average(x => x.Prediction) - group.Average(x => x.Actual), 2) })
            .Sum(bin => bin.Count * bin.Error) / samples.Count);
        double ece = Enumerable.Range(0, 10).Select(index =>
        {
            PredictionSample[] bin = samples.Where(sample => sample.Prediction >= index / 10d &&
                (index == 9 ? sample.Prediction <= 1 : sample.Prediction < (index + 1) / 10d)).ToArray();
            return bin.Length == 0 ? 0 : bin.Length / (double)samples.Count *
                Math.Abs(bin.Average(sample => sample.Prediction) - bin.Average(sample => sample.Actual));
        }).Sum();
        return new(samples.Count, logLoss, rmse, rmseBins, Auc(samples), ece);
    }

    private static double Auc(IReadOnlyList<PredictionSample> samples)
    {
        int positives = samples.Count(sample => sample.Actual == 1);
        int negatives = samples.Count - positives;
        if (positives == 0 || negatives == 0)
            return 0.5;
        double favorable = 0;
        foreach (PredictionSample positive in samples.Where(sample => sample.Actual == 1))
            foreach (PredictionSample negative in samples.Where(sample => sample.Actual == 0))
                favorable += positive.Prediction > negative.Prediction ? 1 : positive.Prediction == negative.Prediction ? 0.5 : 0;
        return favorable / (positives * (double)negatives);
    }

    private static IReadOnlyList<RawPrediction> Raw(IReadOnlyList<PredictionSample> samples) => samples
        .GroupBy(sample => sample.UserId)
        .OrderBy(group => group.Key)
        .Select(group => new RawPrediction(
            group.Key,
            group.Select(sample => Math.Round(sample.Prediction, 4)).ToArray(),
            group.Select(sample => sample.Actual).ToArray()))
        .ToArray();

    private static double Clamp(double probability) => Math.Clamp(probability, 1e-7, 1 - 1e-7);
}
