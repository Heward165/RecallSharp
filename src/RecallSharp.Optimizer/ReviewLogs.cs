using System.Globalization;
using System.Text;

namespace RecallSharp.Optimization;

/// <summary>One rating imported from an application's durable review history.</summary>
public sealed record FsrsReviewLog(long CardId, DateTimeOffset ReviewedAt, FsrsRating Rating);

/// <summary>Chronologically ordered reviews for one card.</summary>
public sealed record FsrsCardHistory(long CardId, IReadOnlyList<FsrsReviewLog> Reviews)
{
    /// <summary>Validates and groups flat review records by card.</summary>
    public static IReadOnlyList<FsrsCardHistory> Group(IEnumerable<FsrsReviewLog> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        FsrsReviewLog[] materialized = reviews.ToArray();
        if (materialized.Any(review => review.ReviewedAt == default ||
                                       review.Rating is < FsrsRating.Again or > FsrsRating.Easy))
            throw new ArgumentException("Review timestamps and ratings must be valid.", nameof(reviews));

        return materialized
            .GroupBy(review => review.CardId)
            .Select(group => new FsrsCardHistory(
                group.Key,
                Array.AsReadOnly(group.OrderBy(review => review.ReviewedAt).ToArray())))
            .OrderBy(history => history.Reviews[0].ReviewedAt)
            .ThenBy(history => history.CardId)
            .ToArray();
    }
}

/// <summary>Interoperable invariant-culture CSV import and export.</summary>
public static class FsrsReviewLogCsv
{
    private const string Header = "card_id,reviewed_at,rating";

    /// <summary>Writes the portable RecallSharp review-log format.</summary>
    public static string Export(IEnumerable<FsrsReviewLog> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        var output = new StringBuilder(Header).AppendLine();
        foreach (FsrsReviewLog review in reviews.OrderBy(review => review.ReviewedAt).ThenBy(review => review.CardId))
        {
            output.Append(review.CardId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(review.ReviewedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(((int)review.Rating).ToString(CultureInfo.InvariantCulture)).AppendLine();
        }

        return output.ToString();
    }

    /// <summary>Reads the portable format and fails closed on malformed rows.</summary>
    public static IReadOnlyList<FsrsReviewLog> Import(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Expected CSV header '{Header}'.");

        var reviews = new List<FsrsReviewLog>();
        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineNumber])) continue;
            string[] fields = lines[lineNumber].Split(',');
            if (fields.Length != 3 ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long cardId) ||
                !DateTimeOffset.TryParseExact(fields[1], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset reviewedAt) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawRating) ||
                rawRating is < 1 or > 4)
                throw new FormatException($"Invalid review-log row {lineNumber + 1}.");

            reviews.Add(new FsrsReviewLog(cardId, reviewedAt.ToUniversalTime(), (FsrsRating)rawRating));
        }

        return reviews;
    }
}

/// <summary>
/// Interchange format used by optimizer ecosystems that represent time as whole days
/// since the preceding review: <c>card_id,rating,delta_t</c>.
/// </summary>
public static class FsrsDeltaReviewCsv
{
    private const string Header = "card_id,rating,delta_t";

    /// <summary>Exports whole-day deltas; sub-day precision is intentionally not representable.</summary>
    public static string Export(IEnumerable<FsrsReviewLog> reviews)
    {
        IReadOnlyList<FsrsCardHistory> histories = FsrsCardHistory.Group(reviews);
        var output = new StringBuilder(Header).AppendLine();
        foreach (FsrsCardHistory history in histories.OrderBy(history => history.CardId))
        {
            DateTimeOffset? prior = null;
            foreach (FsrsReviewLog review in history.Reviews)
            {
                int delta = prior is null ? 0 : Math.Max(0,
                    (int)Math.Floor((review.ReviewedAt - prior.Value).TotalDays));
                output.Append(history.CardId.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(((int)review.Rating).ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(delta.ToString(CultureInfo.InvariantCulture)).AppendLine();
                prior = review.ReviewedAt;
            }
        }

        return output.ToString();
    }

    /// <summary>Imports day-delta histories using a deterministic epoch for each card's first review.</summary>
    public static IReadOnlyList<FsrsReviewLog> Import(string csv, DateTimeOffset epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        if (epoch == default) throw new ArgumentOutOfRangeException(nameof(epoch));
        string[] lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Expected CSV header '{Header}'.");

        var clocks = new Dictionary<long, DateTimeOffset>();
        var reviews = new List<FsrsReviewLog>();
        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineNumber])) continue;
            string[] fields = lines[lineNumber].Split(',');
            if (fields.Length != 3 ||
                !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long cardId) ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawRating) ||
                rawRating is < 1 or > 4 ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta) ||
                delta < 0)
                throw new FormatException($"Invalid delta review row {lineNumber + 1}.");
            DateTimeOffset at = clocks.TryGetValue(cardId, out DateTimeOffset prior)
                ? prior.AddDays(delta)
                : epoch.ToUniversalTime().AddTicks(cardId >= 0 ? cardId % TimeSpan.TicksPerSecond : -(cardId % TimeSpan.TicksPerSecond));
            clocks[cardId] = at;
            reviews.Add(new FsrsReviewLog(cardId, at, (FsrsRating)rawRating));
        }

        return reviews;
    }
}
