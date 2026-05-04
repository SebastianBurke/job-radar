using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IDedupStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<StoredPosting?> GetAsync(string hash, CancellationToken ct = default);

    Task UpsertNewAsync(JobPosting posting, DateTimeOffset now, CancellationToken ct = default);

    Task TouchLastSeenAsync(string hash, DateTimeOffset now, CancellationToken ct = default);

    Task SaveScoreAsync(string hash, ScoringResult score, DateTimeOffset now, CancellationToken ct = default);

    Task SetStatusAsync(string hash, PostingStatus status, DateTimeOffset now, CancellationToken ct = default);

    Task<bool> SetStatusByUrlAsync(string url, PostingStatus status, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<StoredPosting>> ListPendingAsync(CancellationToken ct = default);

    Task<int> ExpireStaleAsync(DateTimeOffset olderThan, DateTimeOffset now, CancellationToken ct = default);
}
