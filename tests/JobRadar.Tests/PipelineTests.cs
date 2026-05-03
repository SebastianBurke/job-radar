using JobRadar.App;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests;

public sealed class PipelineTests
{
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

    private sealed class FakeDedup : IDedupStore
    {
        private readonly HashSet<string> _seen = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> HasSeenAsync(string hash, CancellationToken ct = default) => Task.FromResult(_seen.Contains(hash));
        public Task MarkSeenAsync(string hash, JobPosting posting, DateTimeOffset seenAt, CancellationToken ct = default)
        {
            _seen.Add(hash);
            return Task.CompletedTask;
        }
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

    private static FiltersConfig DefaultFilters(int cap = 200) => new()
    {
        KeywordsRequired = { ".net", "c#", "full stack", "software engineer" },
        LocationAllow = { "remote", "spain", "canada", "europe" },
        LocationDenyPhrases = { "us only", "h1b" },
        MaxScoringCallsPerRun = cap,
    };

    private static JobPosting Posting(string title, string location, string description = "stuff", string company = "Acme") =>
        new("test", company, title, location, $"https://x/{Guid.NewGuid():N}", description);

    [Fact]
    public async Task Filters_keyword_then_location_then_dedupes_then_scores()
    {
        var keep = Posting(".NET Developer", "Remote — Spain");
        var nonKeyword = Posting("Marketing Lead", "Remote — Spain");
        var nonLocation = Posting(".NET Developer", "New York, NY");
        var ineligible = Posting(".NET Developer", "Remote");
        var dropped = Posting(".NET Developer", "Remote — Spain"); // will become duplicate after first run

        var dedup = new FakeDedup();
        var scorer = new FakeScorer(p => new ScoringResult(
            MatchScore: p == ineligible ? 7 : 9,
            Eligibility: p == ineligible ? EligibilityVerdict.Ineligible : EligibilityVerdict.Eligible,
            EligibilityReason: "ok",
            Top3MatchedSkills: new[] { "x", "y", "z" },
            TopConcern: "n/a",
            EstimatedSeniority: "mid",
            LanguageRequired: "english",
            SalaryListed: null,
            RemotePolicy: "remote",
            OneLinePitch: "fit"));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", keep, nonKeyword, nonLocation, ineligible) },
            dedup,
            scorer,
            DefaultFilters(),
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
    public async Task Cost_guard_aborts_when_survivors_exceed_cap()
    {
        var lots = Enumerable.Range(0, 5).Select(i => Posting($".NET Developer {i}", "Remote — Spain")).ToArray();
        var dedup = new FakeDedup();
        var scorer = new FakeScorer(_ => throw new InvalidOperationException("should not be called"));

        var pipeline = new Pipeline(
            new IJobSource[] { new FakeSource("fake", lots) },
            dedup,
            scorer,
            DefaultFilters(cap: 3),
            NullLogger<Pipeline>.Instance);

        var (entries, stats) = await pipeline.RunAsync();

        Assert.Equal(0, scorer.Calls);
        Assert.Empty(entries);
        Assert.Equal(5, stats.Aborted);
    }
}
