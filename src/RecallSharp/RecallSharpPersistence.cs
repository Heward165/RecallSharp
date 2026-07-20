using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecallSharp;

/// <summary>A versioned, self-describing persistence envelope for one card.</summary>
public sealed record RecallSharpDocument(
    int SchemaVersion,
    string SchedulerName,
    string SchedulerVersion,
    FsrsOptions Options,
    FsrsMemoryState State,
    IReadOnlyList<RecallSharpReviewEvent> Reviews)
{
    /// <summary>Current JSON contract emitted by the library.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates a document from an existing state and its audit trail.</summary>
    public static RecallSharpDocument Create(
        FsrsScheduler scheduler,
        FsrsMemoryState state,
        IEnumerable<RecallSharpReviewEvent>? reviews = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(state);
        return new(
            CurrentSchemaVersion,
            scheduler.Name,
            scheduler.Version,
            scheduler.Options,
            state,
            Array.AsReadOnly((reviews ?? []).ToArray()));
    }
}

/// <summary>Auditable input and prediction captured for one completed review.</summary>
public sealed record RecallSharpReviewEvent(
    DateTimeOffset ReviewedAt,
    FsrsRating Rating,
    double PredictedRetrievability,
    FsrsMemoryState ResultingState);

/// <summary>Dependency-free JSON serialization with strict schema validation.</summary>
public static class RecallSharpJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Serializes a current document.</summary>
    public static string Serialize(RecallSharpDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Deserializes and validates a current document.</summary>
    public static RecallSharpDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<RecallSharpDocument>(json, Options)
            ?? throw new ArgumentException("The RecallSharp document is empty.", nameof(json));
        Validate(document);
        return document;
    }

    private static void Validate(RecallSharpDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != RecallSharpDocument.CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported RecallSharp schema {document.SchemaVersion}.");
        if (document.SchedulerName != FsrsOptions.SchedulerName ||
            document.SchedulerVersion != FsrsOptions.SchedulerVersion)
            throw new ArgumentException("The document was produced by an incompatible scheduler.", nameof(document));
        ArgumentNullException.ThrowIfNull(document.Options);
        ArgumentNullException.ThrowIfNull(document.State);
        ArgumentNullException.ThrowIfNull(document.Reviews);
        _ = new FsrsScheduler(document.Options);
        if (document.Reviews.Any(review => review.ReviewedAt == default ||
                                           !double.IsFinite(review.PredictedRetrievability) ||
                                           review.PredictedRetrievability is < 0 or > 1))
            throw new ArgumentException("The review audit trail is invalid.", nameof(document));
    }
}
