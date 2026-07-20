namespace RecallSharp.Optimization;

/// <summary>Specificity levels used by hierarchical parameter fallback.</summary>
public enum FsrsParameterLevel
{
    /// <summary>Population or application defaults.</summary>
    Application,
    /// <summary>Parameters fitted across one user.</summary>
    User,
    /// <summary>Parameters fitted within one deck.</summary>
    Deck,
    /// <summary>Parameters fitted for a card category within a deck.</summary>
    Category
}

/// <summary>One fitted parameter candidate and the evidence supporting it.</summary>
public sealed record FsrsParameterCandidate(
    FsrsParameterLevel Level,
    string ScopeId,
    IReadOnlyList<double> Parameters,
    int PredictiveReviewCount,
    DateTimeOffset FittedAt);

/// <summary>Lookup context ordered from broad user scope to narrow category scope.</summary>
public sealed record FsrsParameterContext(string UserId, string? DeckId = null, string? CategoryId = null);

/// <summary>Minimum evidence required before a fitted scope overrides its parent.</summary>
public sealed record FsrsHierarchyThresholds(
    int UserReviews = 1_000,
    int DeckReviews = 500,
    int CategoryReviews = 250);

/// <summary>Selected parameters with an auditable fallback trail.</summary>
public sealed record FsrsParameterResolution(
    FsrsParameterCandidate Selected,
    IReadOnlyList<string> FallbackReasons);

/// <summary>Selects the most specific adequately supported FSRS-6 parameter set.</summary>
public static class FsrsParameterHierarchy
{
    /// <summary>Resolves category, deck, user, then application candidates in that order.</summary>
    public static FsrsParameterResolution Resolve(
        FsrsParameterContext context,
        IEnumerable<FsrsParameterCandidate> candidates,
        FsrsHierarchyThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.UserId);
        FsrsHierarchyThresholds limits = thresholds ?? new();
        if (limits.UserReviews < 1 || limits.DeckReviews < 1 || limits.CategoryReviews < 1)
            throw new ArgumentOutOfRangeException(nameof(thresholds));

        FsrsParameterCandidate[] available = candidates.ToArray();
        foreach (FsrsParameterCandidate candidate in available)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ScopeId);
            ArgumentNullException.ThrowIfNull(candidate.Parameters);
            if (candidate.Parameters.Count != 21 || candidate.Parameters.Any(value => !double.IsFinite(value)) ||
                candidate.PredictiveReviewCount < 0 || candidate.FittedAt == default)
                throw new ArgumentException("A hierarchy candidate is invalid.", nameof(candidates));
        }

        var attempts = new[]
        {
            (Level: FsrsParameterLevel.Category,
             Id: context.CategoryId is null || context.DeckId is null ? null : $"{context.UserId}/{context.DeckId}/{context.CategoryId}",
             Minimum: limits.CategoryReviews),
            (Level: FsrsParameterLevel.Deck,
             Id: context.DeckId is null ? null : $"{context.UserId}/{context.DeckId}",
             Minimum: limits.DeckReviews),
            (Level: FsrsParameterLevel.User, Id: context.UserId, Minimum: limits.UserReviews)
        };
        var reasons = new List<string>();
        foreach (var attempt in attempts)
        {
            if (attempt.Id is null)
                continue;
            FsrsParameterCandidate? candidate = available
                .Where(item => item.Level == attempt.Level && string.Equals(item.ScopeId, attempt.Id, StringComparison.Ordinal))
                .OrderByDescending(item => item.FittedAt)
                .FirstOrDefault();
            if (candidate is null)
            {
                reasons.Add($"{attempt.Level} scope '{attempt.Id}' has no fitted parameters.");
                continue;
            }

            if (candidate.PredictiveReviewCount < attempt.Minimum)
            {
                reasons.Add($"{attempt.Level} scope '{attempt.Id}' has {candidate.PredictiveReviewCount} reviews; {attempt.Minimum} required.");
                continue;
            }

            return new(candidate, reasons.AsReadOnly());
        }

        FsrsParameterCandidate application = available
            .Where(candidate => candidate.Level == FsrsParameterLevel.Application)
            .OrderByDescending(candidate => candidate.FittedAt)
            .FirstOrDefault()
            ?? new(FsrsParameterLevel.Application, "default", FsrsOptions.DefaultParameters, 0, DateTimeOffset.UnixEpoch);
        return new(application, reasons.AsReadOnly());
    }
}
