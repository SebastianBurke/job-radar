using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using Microsoft.Extensions.Logging;

namespace JobRadar.App;

public sealed class PipelineStats
{
    public Dictionary<string, int> FetchedPerSource { get; } = new();
    public int FilteredOutByKeyword { get; set; }
    public int FilteredOutByLocation { get; set; }
    public int DedupedOut { get; set; }
    public int Scored { get; set; }
    public int DroppedIneligible { get; set; }
    public int Aborted { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class Pipeline
{
    private readonly IEnumerable<IJobSource> _sources;
    private readonly IDedupStore _dedup;
    private readonly IScorer _scorer;
    private readonly FiltersConfig _filters;
    private readonly ILogger<Pipeline> _logger;
    private readonly Filters.PostingFilters _postingFilters;

    public Pipeline(
        IEnumerable<IJobSource> sources,
        IDedupStore dedup,
        IScorer scorer,
        FiltersConfig filters,
        ILogger<Pipeline> logger)
    {
        _sources = sources;
        _dedup = dedup;
        _scorer = scorer;
        _filters = filters;
        _logger = logger;
        _postingFilters = new Filters.PostingFilters(filters);
    }

    public async Task<(IReadOnlyList<DigestEntry> Entries, PipelineStats Stats)> RunAsync(CancellationToken ct = default)
    {
        var stats = new PipelineStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await _dedup.InitializeAsync(ct);

        var survivors = new List<JobPosting>();

        foreach (var source in _sources)
        {
            var fetched = 0;
            try
            {
                await foreach (var posting in source.FetchAsync(ct))
                {
                    fetched++;
                    if (!_postingFilters.PassesKeyword(posting))
                    {
                        stats.FilteredOutByKeyword++;
                        continue;
                    }
                    if (!_postingFilters.PassesLocation(posting))
                    {
                        stats.FilteredOutByLocation++;
                        continue;
                    }

                    if (await _dedup.HasSeenAsync(posting.Hash, ct))
                    {
                        stats.DedupedOut++;
                        continue;
                    }

                    survivors.Add(posting);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {Source} crashed; continuing with other sources.", source.Name);
            }
            stats.FetchedPerSource[source.Name] = fetched;
        }

        if (survivors.Count > _filters.MaxScoringCallsPerRun)
        {
            stats.Aborted = survivors.Count;
            _logger.LogError(
                "Cost guard tripped: {Count} survivors exceed cap of {Cap}. Aborting before any scoring call.",
                survivors.Count,
                _filters.MaxScoringCallsPerRun);
            stats.Duration = sw.Elapsed;
            return (Array.Empty<DigestEntry>(), stats);
        }

        var entries = new List<DigestEntry>(survivors.Count);
        await foreach (var (posting, score) in _scorer.ScoreManyAsync(survivors, ct))
        {
            stats.Scored++;
            if (score.Eligibility == EligibilityVerdict.Ineligible)
            {
                stats.DroppedIneligible++;
                continue;
            }
            entries.Add(new DigestEntry(posting, score));
            await _dedup.MarkSeenAsync(posting.Hash, posting, DateTimeOffset.UtcNow, ct);
        }

        entries.Sort((a, b) => b.Score.MatchScore.CompareTo(a.Score.MatchScore));

        stats.Duration = sw.Elapsed;
        return (entries, stats);
    }
}
