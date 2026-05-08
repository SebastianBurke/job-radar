namespace JobRadar.Core.Models;

public sealed record StoredPosting(
    string Hash,
    JobPosting Posting,
    PostingStatus Status,
    ScoringResult? CachedScore,
    DateTimeOffset SeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? StatusAt,
    DateTimeOffset? LiveCheckAt = null);
