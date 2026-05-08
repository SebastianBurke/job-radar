using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IDedupStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<StoredPosting?> GetAsync(string hash, CancellationToken ct = default);

    Task UpsertNewAsync(JobPosting posting, DateTimeOffset now, CancellationToken ct = default);

    Task TouchLastSeenAsync(string hash, DateTimeOffset now, CancellationToken ct = default);

    Task SaveScoreAsync(string hash, ScoringResult score, DateTimeOffset now, string? scoringInputsHash = null, CancellationToken ct = default);

    /// <summary>
    /// Counts pending postings whose cached score was computed under a different
    /// <c>scoring_inputs_hash</c>. Used at run start to log how many cached
    /// scores are about to be invalidated by a cv.md / rubric edit.
    /// </summary>
    Task<int> CountStaleCachesAsync(string currentScoringInputsHash, CancellationToken ct = default);

    Task SetStatusAsync(string hash, PostingStatus status, DateTimeOffset now, CancellationToken ct = default);

    Task<bool> SetStatusByUrlAsync(string url, PostingStatus status, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Records that the ATS live-check ran for this posting. Caches the timestamp so
    /// subsequent runs can skip re-fetching dead/already-checked URLs.
    /// </summary>
    Task MarkLiveCheckedAsync(string hash, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<StoredPosting>> ListPendingAsync(CancellationToken ct = default);

    Task<int> ExpireStaleAsync(DateTimeOffset olderThan, DateTimeOffset now, CancellationToken ct = default);
}
