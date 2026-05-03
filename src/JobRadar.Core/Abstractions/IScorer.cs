using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface IScorer
{
    Task<ScoringResult> ScoreAsync(JobPosting posting, CancellationToken ct = default);

    IAsyncEnumerable<(JobPosting Posting, ScoringResult Score)> ScoreManyAsync(
        IEnumerable<JobPosting> postings,
        CancellationToken ct = default);
}
