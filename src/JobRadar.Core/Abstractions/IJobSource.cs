using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IJobSource
{
    string Name { get; }

    IAsyncEnumerable<JobPosting> FetchAsync(CancellationToken ct = default);
}
