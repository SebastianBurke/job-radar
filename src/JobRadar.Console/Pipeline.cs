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
    public int Scored { get; set; }
    public int DroppedIneligible { get; set; }
    public int Aborted { get; set; }
    public int Pending { get; set; }
    public int Resurrected { get; set; }
    public int Expired { get; set; }
    public int MarkedFromYaml { get; set; }
    public int LiveCheckLive { get; set; }
    public int LiveCheckDead { get; set; }
    public int LiveCheckUnknown { get; set; }
    public int LiveCheckDropped { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class Pipeline
{
    private readonly IEnumerable<IJobSource> _sources;
    private readonly IDedupStore _dedup;
    private readonly IScorer _scorer;
    private readonly IAtsLiveChecker _liveChecker;
    private readonly FiltersConfig _filters;
    private readonly SourcesConfig _sourcesConfig;
    private readonly RuntimeOptions _options;
    private readonly ILogger<Pipeline> _logger;
    private readonly Filters.PostingFilters _postingFilters;

    public Pipeline(
        IEnumerable<IJobSource> sources,
        IDedupStore dedup,
        IScorer scorer,
        IAtsLiveChecker liveChecker,
        FiltersConfig filters,
        SourcesConfig sourcesConfig,
        RuntimeOptions options,
        ILogger<Pipeline> logger)
    {
        _sources = sources;
        _dedup = dedup;
        _scorer = scorer;
        _liveChecker = liveChecker;
        _filters = filters;
        _sourcesConfig = sourcesConfig;
        _options = options;
        _logger = logger;
        _postingFilters = new Filters.PostingFilters(filters);
    }

    public async Task<(IReadOnlyList<DigestEntry> Entries, PipelineStats Stats)> RunAsync(CancellationToken ct = default)
    {
        var stats = new PipelineStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;

        await _dedup.InitializeAsync(ct);

        // Phase 0a: reconcile data/applied.yml so user-driven status updates are applied
        // before the expiry pass and before fetching.
        var appliedYamlPath = Path.Combine(_options.RepoRoot, "data", "applied.yml");
        try
        {
            var doc = AppliedYamlStore.Load(appliedYamlPath);
            foreach (var entry in doc.Applied)
            {
                if (string.IsNullOrWhiteSpace(entry.Url)) continue;
                if (await _dedup.SetStatusByUrlAsync(entry.Url, PostingStatus.Applied, now, ct))
                {
                    stats.MarkedFromYaml++;
                }
                else
                {
                    _logger.LogWarning("applied.yml: no matching DB row for applied URL {Url}", entry.Url);
                }
            }
            foreach (var entry in doc.Dismissed)
            {
                if (string.IsNullOrWhiteSpace(entry.Url)) continue;
                if (await _dedup.SetStatusByUrlAsync(entry.Url, PostingStatus.Dismissed, now, ct))
                {
                    stats.MarkedFromYaml++;
                }
                else
                {
                    _logger.LogWarning("applied.yml: no matching DB row for dismissed URL {Url}", entry.Url);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile {Path}; continuing.", appliedYamlPath);
        }

        // Phase 0b: auto-expire pending postings the source feeds have stopped listing.
        var graceDays = Math.Max(0, _filters.PendingGraceDays);
        var cutoff = now.AddDays(-graceDays);
        stats.Expired = await _dedup.ExpireStaleAsync(cutoff, now, ct);

        // Phase 1: fetch + filter, classify each posting against the store.
        var newPostings = new List<JobPosting>();
        var pendingHashesSeen = new HashSet<string>();

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

                    var existing = await _dedup.GetAsync(posting.Hash, ct);
                    if (existing is null)
                    {
                        newPostings.Add(posting);
                        continue;
                    }

                    switch (existing.Status)
                    {
                        case PostingStatus.Pending:
                            await _dedup.TouchLastSeenAsync(posting.Hash, now, ct);
                            if (existing.CachedScore is null)
                            {
                                // Legacy row from the v2 migration: pending but never scored.
                                // Queue for first-time scoring; the upsert below is a no-op.
                                newPostings.Add(posting);
                            }
                            else
                            {
                                pendingHashesSeen.Add(posting.Hash);
                            }
                            break;
                        case PostingStatus.Expired:
                            // Resurrection: source still lists it, so treat as pending again.
                            await _dedup.SetStatusAsync(posting.Hash, PostingStatus.Pending, now, ct);
                            await _dedup.TouchLastSeenAsync(posting.Hash, now, ct);
                            stats.Resurrected++;
                            if (existing.CachedScore is null)
                            {
                                newPostings.Add(posting);
                            }
                            else
                            {
                                pendingHashesSeen.Add(posting.Hash);
                            }
                            break;
                        case PostingStatus.Applied:
                        case PostingStatus.Dismissed:
                        case PostingStatus.Dead:
                            // Decided. Never resurrect; never include in digest.
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {Source} crashed; continuing with other sources.", source.Name);
            }
            stats.FetchedPerSource[source.Name] = fetched;
        }

        // Phase 2: cost guard — applies only to brand-new postings (the only group that
        // triggers an Anthropic API call). Pending carry-overs reuse cached scores.
        if (newPostings.Count > _filters.MaxScoringCallsPerRun)
        {
            stats.Aborted = newPostings.Count;
            _logger.LogError(
                "Cost guard tripped: {Count} new postings exceed cap of {Cap}. Aborting before any scoring call.",
                newPostings.Count,
                _filters.MaxScoringCallsPerRun);
            stats.Duration = sw.Elapsed;
            return (Array.Empty<DigestEntry>(), stats);
        }

        // Phase 3: insert new rows so SaveScoreAsync has a target row.
        foreach (var posting in newPostings)
        {
            await _dedup.UpsertNewAsync(posting, now, ct);
        }

        // Phase 3.5: live-check each new posting against its underlying ATS (or, for
        // aggregator sources, against the apply URL). Postings that 404 under
        // RequireOk are dropped before scoring; BestEffort failures are logged but
        // pass through. The verdict timestamp is cached so dead URLs aren't re-fetched.
        var liveNew = await LiveCheckAsync(newPostings, stats, now, ct);

        // Phase 4: score live new postings.
        var newEntries = new List<DigestEntry>(liveNew.Count);
        await foreach (var (posting, score) in _scorer.ScoreManyAsync(liveNew, ct))
        {
            stats.Scored++;
            // Persist the score regardless of eligibility — keeps diagnostics intact.
            await _dedup.SaveScoreAsync(posting.Hash, score, now, ct);
            if (score.Eligibility == EligibilityVerdict.Ineligible)
            {
                stats.DroppedIneligible++;
                await _dedup.SetStatusAsync(posting.Hash, PostingStatus.Dismissed, now, ct);
                continue;
            }
            newEntries.Add(new DigestEntry(posting, score, now));
        }

        // Phase 5: assemble carry-over entries for pending postings that were re-encountered.
        var carryEntries = new List<DigestEntry>();
        foreach (var hash in pendingHashesSeen)
        {
            var stored = await _dedup.GetAsync(hash, ct);
            if (stored?.CachedScore is null)
            {
                // Edge case: pending row from a previous v2 run that never persisted a score.
                // Skipping is safe; it won't reappear until something else re-scores it.
                continue;
            }
            carryEntries.Add(new DigestEntry(stored.Posting, stored.CachedScore, stored.SeenAt));
        }
        stats.Pending = carryEntries.Count;

        var entries = newEntries.Concat(carryEntries).ToList();
        entries.Sort((a, b) => b.Score.MatchScore.CompareTo(a.Score.MatchScore));

        stats.Duration = sw.Elapsed;
        return (entries, stats);
    }

    private async Task<List<JobPosting>> LiveCheckAsync(
        List<JobPosting> newPostings,
        PipelineStats stats,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (newPostings.Count == 0) return newPostings;

        // Fan out per posting; the per-host rate limiter inside the checker serializes
        // calls to the same ATS so the load stays at ~1 req/sec per host.
        var checkTasks = newPostings.Select(async posting =>
        {
            var mode = _sourcesConfig.LiveCheckModeFor(posting.Source);
            var result = await _liveChecker.CheckAsync(posting, mode, ct);
            return (Posting: posting, Mode: mode, Result: result);
        });
        var checks = await Task.WhenAll(checkTasks);

        var liveNew = new List<JobPosting>(newPostings.Count);
        foreach (var (posting, mode, result) in checks)
        {
            if (mode != LiveCheckMode.None)
            {
                await _dedup.MarkLiveCheckedAsync(posting.Hash, now, ct);
            }

            switch (result.Verdict)
            {
                case LiveCheckVerdict.Live: stats.LiveCheckLive++; break;
                case LiveCheckVerdict.Dead: stats.LiveCheckDead++; break;
                case LiveCheckVerdict.Unknown: stats.LiveCheckUnknown++; break;
            }

            var passToScore = (mode, result.Verdict) switch
            {
                (LiveCheckMode.None, _) => true,
                (LiveCheckMode.BestEffort, _) => true,
                (LiveCheckMode.RequireOk, LiveCheckVerdict.Live) => true,
                _ => false,
            };

            if (!passToScore)
            {
                stats.LiveCheckDropped++;
                if (result.Verdict == LiveCheckVerdict.Dead)
                {
                    // Permanent: mark Dead so future runs skip without rechecking.
                    await _dedup.SetStatusAsync(posting.Hash, PostingStatus.Dead, now, ct);
                    _logger.LogInformation(
                        "Skipped: ats_404 {Source}/{Company}/{Title} — {Reason}",
                        posting.Source, posting.Company, posting.Title, result.Reason);
                }
                else
                {
                    // Unknown under RequireOk: drop for this run only; row stays Pending so
                    // a future run can re-check once whatever transient issue resolves.
                    _logger.LogWarning(
                        "Live-check unknown {Source}/{Company}/{Title} — {Reason}; deferring to next run.",
                        posting.Source, posting.Company, posting.Title, result.Reason);
                }
                continue;
            }

            if (result.Verdict == LiveCheckVerdict.Dead)
            {
                _logger.LogWarning(
                    "Live-check dead under BestEffort {Source}/{Company}/{Title} — {Reason}; scoring anyway.",
                    posting.Source, posting.Company, posting.Title, result.Reason);
            }

            liveNew.Add(posting);
        }

        return liveNew;
    }
}
