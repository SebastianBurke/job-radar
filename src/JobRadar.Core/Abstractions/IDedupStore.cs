using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IDedupStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<bool> HasSeenAsync(string hash, CancellationToken ct = default);

    Task MarkSeenAsync(string hash, JobPosting posting, DateTimeOffset seenAt, CancellationToken ct = default);
}
