using JobRadar.App;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Scoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests;

public sealed class PipelineTests
{
    /// <summary>
    /// Hash that <see cref="ScoringInputsHasher"/> produces for a repo root with
    /// none of the 4 input files present — i.e. the value <see cref="DefaultOptions"/>
    /// will end up using since it points at a non-existent temp dir. Tests that
    /// pre-populate cached scores need to tag those scores with this hash so the
    /// new cache-invalidation logic doesn't treat them as stale.
    /// </summary>
    private static readonly string EmptyInputsHash = ScoringInputsHasher.Compute(
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    private sealed class FakeSource : IJobSource
    {
        private readonly JobPosting[] _postings;

        public FakeSource(string name, params JobPosting[] postings)
        {
            Name = name;
            _postings = postings;
        }

        public string Name { get; }

        public async IAsyncEnumerable<JobPosting> FetchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var p in _postings)
            {
                await Task.Yield();
                yield return p;
            }
        }
    }

    private sealed record DedupRow(
        JobPosting Posting,
        PostingStatus Status,
        ScoringResult? Score,
        DateTimeOffset SeenAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset? StatusAt,
        string? ScoringInputsHash = null);

    private sealed class FakeDedup : IDedupStore
    {
        public Dictionary<string, DedupRow> Rows { get; } = new();

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<StoredPosting?> GetAsync(string hash, CancellationToken ct = default)
        {
            if (!Rows.TryGetValue(hash, out var r))
            {
                return Task.FromResult<StoredPosting?>(null);
            }
            return Task.FromResult<StoredPosting?>(new StoredPosting(hash, r.Posting, r.Status, r.Score, r.SeenAt, r.LastSeenAt, r.StatusAt, ScoringInputsHash: r.ScoringInputsHash));
        }

        public Task UpsertNewAsync(JobPosting posting, DateTimeOffset now, CancellationToken ct = default)
        {
            if (!Rows.ContainsKey(posting.Hash))
            {
                Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Pending, null, now, now, now);
            }
            return Task.CompletedTask;
        }

        public Task TouchLastSeenAsync(string hash, DateTimeOffset now, CancellationToken ct = default)
        {
            if (Rows.TryGetValue(hash, out var r))
            {
                Rows[hash] = r with { LastSeenAt = now };
            }
            return Task.CompletedTask;
        }

        public Dictionary<string, string?> ScoringInputsHashByHash { get; } = new();
        public Task SaveScoreAsync(string hash, ScoringResult score, DateTimeOffset now, string? scoringInputsHash = null, CancellationToken ct = default)
        {
            if (Rows.TryGetValue(hash, out var r))
            {
                Rows[hash] = r with { Score = score, LastSeenAt = now, ScoringInputsHash = scoringInputsHash };
            }
            ScoringInputsHashByHash[hash] = scoringInputsHash;
            return Task.CompletedTask;
        }

        public Task<int> CountStaleCachesAsync(string currentScoringInputsHash, CancellationToken ct = default)
        {
            var count = 0;
            foreach (var (_, v) in Rows)
            {
                if (v.Status == PostingStatus.Pending
                    && v.Score is not null
                    && !string.Equals(v.ScoringInputsHash ?? string.Empty, currentScoringInputsHash, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return Task.FromResult(count);
        }

        public Task SetStatusAsync(string hash, PostingStatus status, DateTimeOffset now, CancellationToken ct = default)
        {
            if (Rows.TryGetValue(hash, out var r))
            {
                Rows[hash] = r with { Status = status, StatusAt = now };
            }
            return Task.CompletedTask;
        }

        public Task<bool> SetStatusByUrlAsync(string url, PostingStatus status, DateTimeOffset now, CancellationToken ct = default)
        {
            var hit = false;
            foreach (var (k, v) in Rows.ToArray())
            {
                if (string.Equals(v.Posting.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    Rows[k] = v with { Status = status, StatusAt = now };
                    hit = true;
                }
            }
            return Task.FromResult(hit);
        }

        public Task<IReadOnlyList<StoredPosting>> ListPendingAsync(CancellationToken ct = default)
        {
            IReadOnlyList<StoredPosting> result = Rows
                .Where(kv => kv.Value.Status == PostingStatus.Pending)
                .Select(kv => new StoredPosting(kv.Key, kv.Value.Posting, kv.Value.Status, kv.Value.Score, kv.Value.SeenAt, kv.Value.LastSeenAt, kv.Value.StatusAt, ScoringInputsHash: kv.Value.ScoringInputsHash))
                .ToList();
            return Task.FromResult(result);
        }

        public Task<int> ExpireStaleAsync(DateTimeOffset olderThan, DateTimeOffset now, CancellationToken ct = default)
        {
            var count = 0;
            foreach (var (k, v) in Rows.ToArray())
            {
                if (v.Status == PostingStatus.Pending && v.LastSeenAt < olderThan)
                {
                    Rows[k] = v with { Status = PostingStatus.Expired, StatusAt = now };
                    count++;
                }
            }
            return Task.FromResult(count);
        }

        public Dictionary<string, DateTimeOffset> LiveCheckedAt { get; } = new();
        public Task MarkLiveCheckedAsync(string hash, DateTimeOffset now, CancellationToken ct = default)
        {
            LiveCheckedAt[hash] = now;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveChecker : IAtsLiveChecker
    {
        private readonly Func<JobPosting, LiveCheckResult> _verdict;
        public int Calls;

        public FakeLiveChecker() : this(_ => LiveCheckResult.Live()) { }
        public FakeLiveChecker(Func<JobPosting, LiveCheckResult> verdict) => _verdict = verdict;

        public Task<LiveCheckResult> CheckAsync(JobPosting posting, LiveCheckMode mode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (mode == LiveCheckMode.None) return Task.FromResult(LiveCheckResult.Skipped());
            return Task.FromResult(_verdict(posting));
        }
    }

    private static SourcesConfig DefaultSources(LiveCheckMode? overrideAll = null)
    {
        var s = new SourcesConfig();
        if (overrideAll is { } mode)
        {
            var raw = mode switch
            {
                LiveCheckMode.RequireOk => "require_ok",
                LiveCheckMode.BestEffort => "best_effort",
                LiveCheckMode.None => "none",
                _ => "none",
            };
            // Cover the FakeSource (name="test") and any common source names tests rely on.
            s.LiveCheck["test"] = raw;
        }
        return s;
    }

    private sealed class FakeScorer : IScorer
    {
        private readonly Func<JobPosting, ScoringResult> _score;
        public int Calls;

        public FakeScorer(Func<JobPosting, ScoringResult> score) => _score = score;

        public Task<ScoringResult> ScoreAsync(JobPosting posting, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_score(posting));
        }

        public async IAsyncEnumerable<(JobPosting Posting, ScoringResult Score)> ScoreManyAsync(
            IEnumerable<JobPosting> postings,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var p in postings)
            {
                yield return (p, await ScoreAsync(p, ct));
            }
        }
    }

    private static FiltersConfig DefaultFilters(int cap = 200, int graceDays = 7) => new()
    {
        KeywordsCore = { ".net", "c#" },
        KeywordsBroad = { "software engineer", "full stack" },
        TechContextHints = { ".net", "c#", "azure" },
        LocationAllow = { "remote", "spain", "canada", "europe" },
        LocationDenyPhrases = { "us only", "h1b" },
        MaxScoringCallsPerRun = cap,
        PendingGraceDays = graceDays,
    };

    private static RuntimeOptions DefaultOptions() => new()
    {
        RepoRoot = Path.Combine(Path.GetTempPath(), $"job-radar-pipeline-test-{Guid.NewGuid():N}"),
    };

    private static JobPosting Posting(string title, string location, string description = "stuff", string company = "Acme", string? url = null) =>
        new("test", company, title, location, url ?? $"https://x/{Guid.NewGuid():N}", description);

    private static ScoringResult Eligible(int score = 9) => new(
        MatchScore: score,
        Eligibility: EligibilityVerdict.Eligible,
        EligibilityReason: "ok",
        Top3MatchedSkills: new[] { "x", "y", "z" },
        TopConcern: "n/a",
        EstimatedSeniority: "mid",
        LanguageRequired: "english",
        SalaryListed: null,
        RemotePolicy: "remote",
        OneLinePitch: "fit");

    private static ScoringResult Ineligible() => new(
        MatchScore: 7,
        Eligibility: EligibilityVerdict.Ineligible,
        EligibilityReason: "wrong",
        Top3MatchedSkills: Array.Empty<string>(),
        TopConcern: "n/a",
        EstimatedSeniority: "mid",
        LanguageRequired: "english",
        SalaryListed: null,
        RemotePolicy: "remote",
        OneLinePitch: null);

    [Fact]
    public async Task Filters_keyword_then_location_then_scores_eligible_only()
    {
        var keep = Posting(".NET Developer", "Remote — Spain");
        var nonKeyword = Posting("Marketing Lead", "Remote — Spain");
        var nonLocation = Posting(".NET Developer", "New York, NY");
        var ineligible = Posting(".NET Developer", "Remote");

        var dedup = new FakeDedup();
        var scorer = new FakeScorer(p => p == ineligible ? Ineligible() : Eligible());

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", keep, nonKeyword, nonLocation, ineligible) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(1, stats.FilteredOutByKeyword);
        Assert.Equal(1, stats.FilteredOutByLocation);
        Assert.Equal(2, stats.Scored);
        Assert.Equal(1, stats.DroppedIneligible);
        Assert.Single(entries);
        Assert.Equal(".NET Developer", entries[0].Posting.Title);
        Assert.Equal(0, stats.Aborted);
    }

    [Fact]
    public async Task Cost_guard_aborts_when_new_postings_exceed_cap()
    {
        var lots = Enumerable.Range(0, 5).Select(i => Posting($".NET Developer {i}", "Remote — Spain")).ToArray();
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => throw new InvalidOperationException("should not be called"));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", lots) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(cap: 3),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(0, scorer.Calls);
        Assert.Empty(entries);
        Assert.Equal(5, stats.Aborted);
    }

    [Fact]
    public async Task Pending_carries_over_to_next_run_without_rescoring()
    {
        var keep = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => Eligible(8));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", keep) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (firstEntries, firstStats) = await pipeline.RunAsync();
        Assert.Single(firstEntries);
        Assert.Equal(1, firstStats.Scored);

        // Second run: same posting from feed; must not re-score, must still appear.
        var (secondEntries, secondStats) = await pipeline.RunAsync();
        Assert.Equal(1, scorer.Calls);
        Assert.Equal(0, secondStats.Scored);
        Assert.Single(secondEntries);
        Assert.Equal(1, secondStats.Pending);
        Assert.Equal(keep.Url, secondEntries[0].Posting.Url);
    }

    [Fact]
    public async Task Cost_guard_only_counts_new_postings_not_pending_carryovers()
    {
        var pending1 = Posting(".NET Developer 1", "Remote — Spain");
        var pending2 = Posting(".NET Developer 2", "Remote — Spain");
        var pending3 = Posting(".NET Developer 3", "Remote — Spain");
        var pending4 = Posting(".NET Developer 4", "Remote — Spain");
        var fresh = Posting(".NET Developer Fresh", "Remote — Spain");

        var dedup = new FakeDedup();
        var seenAt = DateTimeOffset.UtcNow;
        foreach (var p in new[] { pending1, pending2, pending3, pending4 })
        {
            dedup.Rows[p.Hash] = new DedupRow(p, PostingStatus.Pending, Eligible(7), seenAt, seenAt, seenAt, ScoringInputsHash: EmptyInputsHash);
        }

        var scorer = new FakeScorer(_ => Eligible(9));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", pending1, pending2, pending3, pending4, fresh) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(cap: 1),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(0, stats.Aborted);
        Assert.Equal(1, stats.Scored);
        Assert.Equal(4, stats.Pending);
        Assert.Equal(5, entries.Count);
    }

    [Fact]
    public async Task Ineligible_new_posting_is_dismissed_and_does_not_resurface()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => Ineligible());

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", posting) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        await pipeline.RunAsync();
        Assert.Equal(PostingStatus.Dismissed, dedup.Rows[posting.Hash].Status);

        // Re-run with same posting: must not re-score, must not appear.
        var (entries, stats) = await pipeline.RunAsync();
        Assert.Equal(1, scorer.Calls);
        Assert.Empty(entries);
        Assert.Equal(0, stats.Pending);
    }

    [Fact]
    public async Task Expired_posting_is_resurrected_when_feed_relists_it()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var seenAt = DateTimeOffset.UtcNow.AddDays(-30);
        dedup.Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Expired, Eligible(8), seenAt, seenAt, seenAt, ScoringInputsHash: EmptyInputsHash);

        var scorer = new FakeScorer(_ => throw new InvalidOperationException("should not be called for resurrected entry"));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", posting) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(0, scorer.Calls);
        Assert.Equal(PostingStatus.Pending, dedup.Rows[posting.Hash].Status);
        Assert.Equal(1, stats.Resurrected);
        Assert.Single(entries);
    }

    [Fact]
    public async Task Disappeared_pending_posting_auto_expires_after_grace_days()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var stale = DateTimeOffset.UtcNow.AddDays(-10);
        dedup.Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Pending, Eligible(8), stale, stale, stale);

        var scorer = new FakeScorer(_ => throw new InvalidOperationException("should not be called"));

        // Empty source feed — posting has disappeared.
        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake") },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(graceDays: 7),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(PostingStatus.Expired, dedup.Rows[posting.Hash].Status);
        Assert.Equal(1, stats.Expired);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Legacy_pending_with_null_score_gets_scored_when_feed_relists_it()
    {
        // Simulates the v2 migration outcome: row exists, status=pending, but no cached score.
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var seenAt = DateTimeOffset.UtcNow.AddDays(-3);
        dedup.Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Pending, Score: null, seenAt, seenAt, seenAt);

        var scorer = new FakeScorer(_ => Eligible(8));
        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", posting) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(1, scorer.Calls);
        Assert.Equal(1, stats.Scored);
        Assert.Single(entries);
        Assert.NotNull(dedup.Rows[posting.Hash].Score);
    }

    [Fact]
    public async Task Editing_cv_md_between_runs_invalidates_the_cached_score()
    {
        // Wires up a real on-disk repo root with all four scoring-inputs files,
        // runs the pipeline twice with a cv.md edit in between, and verifies the
        // second run re-scores the previously-cached posting instead of carrying
        // over the stale verdict.
        var tempRepo = Path.Combine(Path.GetTempPath(), $"job-radar-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRepo, "data"));
        Directory.CreateDirectory(Path.Combine(tempRepo, "prompts"));
        Directory.CreateDirectory(Path.Combine(tempRepo, "config"));

        var cvPath = Path.Combine(tempRepo, "data", "cv.md");
        File.WriteAllText(cvPath, "Initial CV: Senior .NET dev with 10 years experience.");
        File.WriteAllText(Path.Combine(tempRepo, "data", "eligibility.md"), "elig");
        File.WriteAllText(Path.Combine(tempRepo, "prompts", "scoring-prompt.md"), "## System\nbe terse\n## User\n{{cv}} {{posting.title}}");
        File.WriteAllText(Path.Combine(tempRepo, "config", "filters.yml"), "stack_signals:\n  primary: []\n");

        try
        {
            var keep = Posting(".NET Developer", "Remote — Spain");
            var dedup = new FakeDedup();
            var scorer = new FakeScorer(_ => Eligible(8));

            var pipeline = new Pipeline(
                new IJobSource[] { new FakeSource("fake", keep) },
                dedup,
                scorer,
                new FakeLiveChecker(),
                DefaultFilters(),
                DefaultSources(),
                new RuntimeOptions { RepoRoot = tempRepo },
                NullLogger<Pipeline>.Instance);

            // First run scores once and caches with the initial scoring_inputs_hash.
            var (firstEntries, firstStats) = await pipeline.RunAsync();
            Assert.Single(firstEntries);
            Assert.Equal(1, firstStats.Scored);
            Assert.Equal(1, scorer.Calls);
            var firstHash = firstStats.ScoringInputsHash;
            Assert.Equal(firstHash, dedup.Rows[keep.Hash].ScoringInputsHash);

            // Edit cv.md — the only scoring input that changes.
            File.WriteAllText(cvPath, "Corrected CV: 0 years production .NET, junior-to-mid target only.");

            var (secondEntries, secondStats) = await pipeline.RunAsync();
            Assert.NotEqual(firstHash, secondStats.ScoringInputsHash);
            Assert.Equal(2, scorer.Calls);              // re-scored, not carried over
            Assert.Equal(1, secondStats.Scored);
            Assert.Equal(1, secondStats.RescoredDueToInputsChange);
            Assert.Equal(0, secondStats.Pending);       // no carry-over
            Assert.Single(secondEntries);
            Assert.Equal(secondStats.ScoringInputsHash, dedup.Rows[keep.Hash].ScoringInputsHash);
        }
        finally
        {
            if (Directory.Exists(tempRepo)) Directory.Delete(tempRepo, recursive: true);
        }
    }

    [Fact]
    public async Task Same_scoring_inputs_across_runs_carries_cached_score_through()
    {
        // Counterpart to the cv.md-edit test: when the inputs files don't change,
        // the second run must reuse the cached score (not re-score) so we keep
        // amortising Anthropic costs across daily runs.
        var keep = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => Eligible(8));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", keep) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        await pipeline.RunAsync();
        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(1, scorer.Calls);                     // scored once total
        Assert.Equal(0, stats.Scored);                     // second run: 0 new score calls
        Assert.Equal(0, stats.RescoredDueToInputsChange);
        Assert.Equal(1, stats.Pending);
        Assert.Single(entries);
    }

    [Fact]
    public async Task RescoreAll_flag_forces_rescoring_even_when_hash_matches()
    {
        // Escape hatch: --rescore-all bypasses the hash comparison. Useful for
        // debugging the cache logic itself or for an unconditional manual flush.
        var keep = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var seenAt = DateTimeOffset.UtcNow;
        // Pre-cache a score under the same hash the pipeline will compute, so
        // without the flag the row would carry over.
        dedup.Rows[keep.Hash] = new DedupRow(keep, PostingStatus.Pending, Eligible(7), seenAt, seenAt, seenAt, ScoringInputsHash: EmptyInputsHash);

        var scorer = new FakeScorer(_ => Eligible(9));
        var opts = DefaultOptions();
        opts.RescoreAll = true;

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", keep) },
            dedup,
            scorer,
            new FakeLiveChecker(),
            DefaultFilters(),
            DefaultSources(),
            opts,
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(1, scorer.Calls);
        Assert.Equal(1, stats.Scored);
        Assert.Equal(1, stats.RescoredDueToFlag);
        Assert.Equal(0, stats.RescoredDueToInputsChange);
        Assert.Single(entries);
    }

    [Fact]
    public async Task LiveCheck_RequireOk_dead_posting_is_dropped_and_marked_dead()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => throw new InvalidOperationException("must not score dead postings"));
        var checker = new FakeLiveChecker(_ => LiveCheckResult.Dead("simulated 404"));

        var sources = new SourcesConfig();
        sources.LiveCheck["test"] = "require_ok";

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("test", posting) },
            dedup,
            scorer,
            checker,
            DefaultFilters(),
            sources,
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Empty(entries);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(1, stats.LiveCheckDead);
        Assert.Equal(1, stats.LiveCheckDropped);
        Assert.Equal(PostingStatus.Dead, dedup.Rows[posting.Hash].Status);
        Assert.True(dedup.LiveCheckedAt.ContainsKey(posting.Hash));
    }

    [Fact]
    public async Task LiveCheck_BestEffort_dead_posting_still_gets_scored()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => Eligible(8));
        var checker = new FakeLiveChecker(_ => LiveCheckResult.Dead("simulated 404"));

        var sources = new SourcesConfig();
        sources.LiveCheck["test"] = "best_effort";

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("test", posting) },
            dedup,
            scorer,
            checker,
            DefaultFilters(),
            sources,
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Single(entries);
        Assert.Equal(1, scorer.Calls);
        Assert.Equal(1, stats.LiveCheckDead);
        Assert.Equal(0, stats.LiveCheckDropped);
        // BestEffort mode does not mark Dead — the row stays Pending so the score lands.
        Assert.NotEqual(PostingStatus.Dead, dedup.Rows[posting.Hash].Status);
    }

    [Fact]
    public async Task LiveCheck_RequireOk_unknown_drops_without_marking_dead()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => throw new InvalidOperationException("must not score on unknown"));
        var checker = new FakeLiveChecker(_ => LiveCheckResult.Unknown("transient HTTP 500"));

        var sources = new SourcesConfig();
        sources.LiveCheck["test"] = "require_ok";

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("test", posting) },
            dedup,
            scorer,
            checker,
            DefaultFilters(),
            sources,
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Empty(entries);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(1, stats.LiveCheckUnknown);
        Assert.Equal(1, stats.LiveCheckDropped);
        // Unknown leaves the row Pending so a future run can re-check.
        Assert.Equal(PostingStatus.Pending, dedup.Rows[posting.Hash].Status);
    }

    [Fact]
    public async Task LiveCheck_None_passes_through_without_calling_checker()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => Eligible(8));
        var checker = new FakeLiveChecker(_ => throw new InvalidOperationException("must not check when mode=None"));

        var sources = new SourcesConfig();
        sources.LiveCheck["test"] = "none";

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("test", posting) },
            dedup,
            scorer,
            checker,
            DefaultFilters(),
            sources,
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Single(entries);
        Assert.Equal(1, scorer.Calls);
        // None mode short-circuits inside the checker (FakeLiveChecker returns Skipped).
        Assert.Equal(0, stats.LiveCheckDead);
        Assert.Equal(0, stats.LiveCheckUnknown);
        Assert.False(dedup.LiveCheckedAt.ContainsKey(posting.Hash));
    }

    [Fact]
    public async Task LiveCheck_Dead_status_does_not_resurrect_on_reencounter()
    {
        var posting = Posting(".NET Developer", "Remote — Spain");
        var dedup = new FakeDedup();
        var deadAt = DateTimeOffset.UtcNow.AddDays(-1);
        dedup.Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Dead, Score: null, deadAt, deadAt, deadAt);

        var scorer = new FakeScorer(_ => throw new InvalidOperationException("dead postings must not score"));
        var checker = new FakeLiveChecker(_ => throw new InvalidOperationException("dead postings must not re-check"));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("test", posting) },
            dedup,
            scorer,
            checker,
            DefaultFilters(),
            DefaultSources(),
            DefaultOptions(),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Empty(entries);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal(0, checker.Calls);
        Assert.Equal(PostingStatus.Dead, dedup.Rows[posting.Hash].Status);
    }

    [Fact]
    public async Task Yaml_reconciliation_marks_applied_then_no_carryover()
    {
        var posting = Posting(".NET Developer", "Remote — Spain", url: "https://example.com/jobs/abc");
        var dedup = new FakeDedup();
        var seenAt = DateTimeOffset.UtcNow;
        dedup.Rows[posting.Hash] = new DedupRow(posting, PostingStatus.Pending, Eligible(8), seenAt, seenAt, seenAt, ScoringInputsHash: EmptyInputsHash);

        var tempRepo = Path.Combine(Path.GetTempPath(), $"job-radar-yaml-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRepo, "data"));
        try
        {
            AppliedYamlStore.Append(Path.Combine(tempRepo, "data", "applied.yml"), "applied", posting.Url, "test", DateTimeOffset.UtcNow);

            var scorer = new FakeScorer(_ => throw new InvalidOperationException("should not be called"));
            var pipeline = new Pipeline(
                new IJobSource[] { new FakeSource("fake", posting) },
                dedup,
                scorer,
                new FakeLiveChecker(),
                DefaultFilters(),
                DefaultSources(),
                new RuntimeOptions { RepoRoot = tempRepo },
                NullLogger<Pipeline>.Instance);

            var (entries, stats) = await pipeline.RunAsync();

            Assert.Equal(PostingStatus.Applied, dedup.Rows[posting.Hash].Status);
            Assert.Equal(1, stats.MarkedFromYaml);
            Assert.Empty(entries);
        }
        finally
        {
            if (Directory.Exists(tempRepo)) Directory.Delete(tempRepo, recursive: true);
        }
    }
}
