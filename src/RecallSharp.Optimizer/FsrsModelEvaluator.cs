namespace RecallSharp.Optimization;

/// <summary>Predictive accuracy and calibration metrics for one parameter set.</summary>
public sealed record FsrsEvaluationMetrics(
    int ObservationCount,
    double LogLoss,
    double BrierScore,
    double RootMeanSquaredError,
    double ExpectedCalibrationError,
    IReadOnlyList<FsrsCalibrationBin> CalibrationBins);

/// <summary>Observed and predicted recall within one probability range.</summary>
public sealed record FsrsCalibrationBin(
    double LowerBound,
    double UpperBound,
    int Count,
    double MeanPrediction,
    double ObservedRecallRate);

/// <summary>Replays histories and evaluates FSRS recall predictions.</summary>
public static class FsrsModelEvaluator
{
    /// <summary>Evaluates every review after each card's initial rating.</summary>
    public static FsrsEvaluationMetrics Evaluate(
        IEnumerable<FsrsCardHistory> histories,
        IReadOnlyList<double>? parameters = null,
        int calibrationBinCount = 10) =>
        EvaluateWindow(histories, parameters, calibrationBinCount, null, null);

    internal static FsrsEvaluationMetrics EvaluateWindow(
        IEnumerable<FsrsCardHistory> histories,
        IReadOnlyList<double>? parameters,
        int calibrationBinCount,
        DateTimeOffset? from,
        DateTimeOffset? through)
    {
        ArgumentNullException.ThrowIfNull(histories);
        if (calibrationBinCount is < 2 or > 100)
            throw new ArgumentOutOfRangeException(nameof(calibrationBinCount));

        var options = FsrsOptions.Default with
        {
            Parameters = parameters ?? FsrsOptions.DefaultParameters,
            LearningSteps = Array.Empty<TimeSpan>(),
            RelearningSteps = Array.Empty<TimeSpan>()
        };
        var scheduler = new FsrsScheduler(options);
        var samples = new List<(double Prediction, double Actual)>();
        foreach (FsrsCardHistory history in histories)
        {
            ArgumentNullException.ThrowIfNull(history);
            if (history.Reviews.Count == 0) continue;
            FsrsMemoryState state = FsrsMemoryState.New(history.CardId, history.Reviews[0].ReviewedAt);
            DateTimeOffset? prior = null;
            foreach (FsrsReviewLog review in history.Reviews)
            {
                if (review.CardId != history.CardId || (prior is not null && review.ReviewedAt < prior))
                    throw new ArgumentException("Card histories must be ordered and contain one card identifier.", nameof(histories));

                if (state.Stability is not null &&
                    (from is null || review.ReviewedAt >= from) &&
                    (through is null || review.ReviewedAt <= through))
                {
                    double prediction = Math.Clamp(scheduler.Retrievability(state, review.ReviewedAt), 1e-7, 1 - 1e-7);
                    samples.Add((prediction, review.Rating == FsrsRating.Again ? 0 : 1));
                }

                state = scheduler.Review(state, review.Rating, review.ReviewedAt).Current;
                prior = review.ReviewedAt;
            }
        }

        if (samples.Count == 0)
            throw new ArgumentException("At least one card must contain two reviews in the evaluation window.", nameof(histories));

        double logLoss = samples.Average(sample =>
            -(sample.Actual * Math.Log(sample.Prediction) +
              ((1 - sample.Actual) * Math.Log(1 - sample.Prediction))));
        double brier = samples.Average(sample => Math.Pow(sample.Prediction - sample.Actual, 2));
        var bins = Enumerable.Range(0, calibrationBinCount)
            .Select(index =>
            {
                double lower = index / (double)calibrationBinCount;
                double upper = (index + 1) / (double)calibrationBinCount;
                var values = samples.Where(sample =>
                    sample.Prediction >= lower &&
                    (index == calibrationBinCount - 1 ? sample.Prediction <= upper : sample.Prediction < upper)).ToArray();
                return new FsrsCalibrationBin(
                    lower,
                    upper,
                    values.Length,
                    values.Length == 0 ? 0 : values.Average(value => value.Prediction),
                    values.Length == 0 ? 0 : values.Average(value => value.Actual));
            }).ToArray();
        double ece = bins.Sum(bin =>
            (bin.Count / (double)samples.Count) * Math.Abs(bin.MeanPrediction - bin.ObservedRecallRate));
        return new FsrsEvaluationMetrics(
            samples.Count,
            logLoss,
            brier,
            Math.Sqrt(brier),
            ece,
            bins);
    }
}
