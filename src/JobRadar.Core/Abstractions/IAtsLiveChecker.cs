using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IAtsLiveChecker
{
    Task<LiveCheckResult> CheckAsync(JobPosting posting, LiveCheckMode mode, CancellationToken ct = default);
}
