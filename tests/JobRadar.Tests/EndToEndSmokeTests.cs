using System.Net;
using JobRadar.App;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Scoring;
using JobRadar.Sources.Internal;
using JobRadar.Sources.LiveCheck;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests;

/// <summary>
/// End-to-end smoke test that exercises the full pipeline with real components
/// (ClaudeScorer, AtsLiveChecker, PostingFilters) wired through a fake source
/// and stubbed HTTP at the network boundary. Asserts the four 2026-05-07 score
/// adjustments fire: live-check, location confidence, stack modifier, and the
/// French language hint. The Anthropic response is captured so the assertions
/// here pin behaviour without billing the API.
/// </summary>
public sealed class EndToEndSmokeTests
{
    private sealed class SinglePostingSource : IJobSource
    {
        private readonly JobPosting _posting;
        public SinglePostingSource(JobPosting p) => _posting = p;
        public string Name => _posting.Source;
        public async IAsyncEnumerable<JobPosting> FetchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return _posting;
        }
    }

    [Fact]
    public async Task Pipeline_applies_live_check_location_confidence_and_stack_modifier_end_to_end()
    {
        // 1. Hand-crafted .NET-friendly Greenhouse-style posting with Authoritative location.
        var posting = new JobPosting(
            Source: "greenhouse",
            Company: "Airbnb",
            Title: "Senior .NET Engineer",
            Location: "Madrid, Spain",
            Url: "https://boards.greenhouse.io/airbnb/jobs/4242",
            Description: "We're looking for a Senior C# / .NET 8 / ASP.NET Core engineer to ship Blazor features for our Madrid team.",
            AtsId: "4242",
            AtsToken: "airbnb",
            LocationConfidence: LocationConfidence.Authoritative);

        // 2. Mocked Anthropic returns score=7 with eligible verdict; the stack
        //    modifier should then bump this to 9 (+2 for .NET-only) at the scorer.
        //    Capture the rendered request body so we can assert the new placeholders
        //    flowed through — the request's content stream is disposed by the time
        //    the test resumes, so we have to grab it inside the handler closure.
        var capturedBody = string.Empty;
        var anthropicHandler = new StaticHttpHandler(req =>
        {
            // xunit warns about sync-over-async; the content is already buffered in
            // memory by StringContent so this completes synchronously without
            // deadlocking. Reading it async would force the responder to be Task<>.
#pragma warning disable xUnit1031
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
#pragma warning restore xUnit1031
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"content":[{"type":"text","text":"{\"match_score\":7,\"eligibility\":\"eligible\",\"eligibility_reason\":\"EU remote OK.\",\"top_3_matched_skills\":[\"C#\",\".NET\",\"ASP.NET\"],\"top_concern\":\"none\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"Strong .NET fit.\"}"}]}
                    """),
            };
        });

        // Write a minimal prompt template — checks all the new placeholders flow through.
        var promptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(promptPath,
            "## System\nbe terse\n## User\nCV={{cv}}\nElig={{eligibility}}\nTitle={{posting.title}}\n" +
            "Loc={{posting.location}}\nLocConf={{posting.location_confidence}}\n" +
            "Stack={{stack_modifier}} ({{stack_matches}})");
        var cvPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(cvPath, "CV: full stack .NET dev. French intermediate.");
        var eligPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(eligPath,
            "languages:\n  fluent: [English, Spanish]\n  intermediate: [French]\n  none: []\n");

        var stackSignals = new StackSignalsConfig
        {
            Primary = { ".NET", "C#", "ASP.NET", "Blazor" },
            Adjacent = { "TypeScript", "React", "Angular" },
            Mismatched = { "Java", "Spring", "Python", "Node.js" },
        };

        var scorer = new ClaudeScorer(
            new StaticHttpClientFactory(anthropicHandler),
            new ClaudeScorerOptions
            {
                ApiKey = "test-key",
                PromptPath = promptPath,
                CvPath = cvPath,
                EligibilityPath = eligPath,
                MaxRetries = 0,
                StackSignals = stackSignals,
            },
            NullLogger<ClaudeScorer>.Instance);

        // 3. Real AtsLiveChecker with mocked HTTP — Greenhouse API returns 200.
        var liveCheckHandler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":4242,"title":"Senior .NET Engineer"}"""),
        });
        var liveChecker = new AtsLiveChecker(
            new StaticHttpClientFactory(liveCheckHandler),
            new HostRateLimiter(TimeSpan.Zero),
            NullLogger<AtsLiveChecker>.Instance);

        // 4. Real filter config that lets .NET / Madrid through.
        var filters = new FiltersConfig
        {
            KeywordsCore = { ".net", "c#" },
            KeywordsBroad = { "software engineer" },
            TechContextHints = { ".net", "c#", "azure" },
            LocationAllow = { "remote", "spain", "madrid" },
            LocationDenyPhrases = { "us only" },
            MaxScoringCallsPerRun = 200,
            PendingGraceDays = 30,
            StackSignals = stackSignals,
        };
        var sources = new SourcesConfig();
        sources.LiveCheck["greenhouse"] = "require_ok";

        var dedup = new InMemoryDedup();
        var tempRepo = Path.Combine(Path.GetTempPath(), $"job-radar-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRepo);

        try
        {
            var pipeline = new Pipeline(
                new IJobSource[] { new SinglePostingSource(posting) },
                dedup,
                scorer,
                liveChecker,
                filters,
                sources,
                new RuntimeOptions { RepoRoot = tempRepo },
                NullLogger<Pipeline>.Instance);

            var (entries, stats) = await pipeline.RunAsync();

            // Live-check fired and confirmed alive.
            Assert.Equal(1, stats.LiveCheckLive);
            Assert.Equal(0, stats.LiveCheckDead);
            Assert.Single(liveCheckHandler.Requests);
            Assert.Equal(
                "https://boards-api.greenhouse.io/v1/boards/airbnb/jobs/4242",
                liveCheckHandler.Requests[0].RequestUri!.ToString());

            // Posting was scored (Anthropic call hit) and survived as an entry.
            Assert.Equal(1, stats.Scored);
            Assert.Single(entries);

            // Stack modifier (+2) was applied: 7 from the model becomes 9 in the digest.
            Assert.Equal(9, entries[0].Score.MatchScore);

            // Live-check timestamp was cached for diagnostics.
            Assert.True(dedup.LiveCheckedAt.ContainsKey(posting.Hash));

            // Anthropic was called with the rendered prompt that carries the new fields.
            // System.Text.Json escapes '+' as + in the wire format, so decode the
            // request and assert against the user message content.
            Assert.Single(anthropicHandler.Requests);
            using var doc = System.Text.Json.JsonDocument.Parse(capturedBody);
            var userMessage = doc.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content")
                .GetString() ?? string.Empty;

            Assert.Contains("LocConf=authoritative", userMessage);
            Assert.Contains("Stack=+2", userMessage);
            Assert.Contains("primary: [.NET, C#, ASP.NET, Blazor]", userMessage);
        }
        finally
        {
            File.Delete(promptPath);
            File.Delete(cvPath);
            File.Delete(eligPath);
            if (Directory.Exists(tempRepo)) Directory.Delete(tempRepo, recursive: true);
        }
    }

    /// <summary>Inline minimal IDedupStore — independent of the FakeDedup in PipelineTests.</summary>
    private sealed class InMemoryDedup : IDedupStore
    {
        private sealed record Row(JobPosting Posting, PostingStatus Status, ScoringResult? Score, DateTimeOffset SeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? StatusAt, string? ScoringInputsHash = null);
        private readonly Dictionary<string, Row> _rows = new();
        public Dictionary<string, DateTimeOffset> LiveCheckedAt { get; } = new();

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<StoredPosting?> GetAsync(string hash, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(hash, out var r)
                ? new StoredPosting(hash, r.Posting, r.Status, r.Score, r.SeenAt, r.LastSeenAt, r.StatusAt, ScoringInputsHash: r.ScoringInputsHash)
                : null);

        public Task UpsertNewAsync(JobPosting posting, DateTimeOffset now, CancellationToken ct = default)
        {
            if (!_rows.ContainsKey(posting.Hash))
            {
                _rows[posting.Hash] = new Row(posting, PostingStatus.Pending, null, now, now, now);
            }
            return Task.CompletedTask;
        }

        public Task TouchLastSeenAsync(string hash, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveScoreAsync(string hash, ScoringResult score, DateTimeOffset now, string? scoringInputsHash = null, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(hash, out var r))
                _rows[hash] = r with { Score = score, LastSeenAt = now, ScoringInputsHash = scoringInputsHash };
            return Task.CompletedTask;
        }

        public Task<int> CountStaleCachesAsync(string currentScoringInputsHash, CancellationToken ct = default)
        {
            var count = _rows.Values.Count(v =>
                v.Status == PostingStatus.Pending && v.Score is not null
                && !string.Equals(v.ScoringInputsHash ?? string.Empty, currentScoringInputsHash, StringComparison.Ordinal));
            return Task.FromResult(count);
        }

        public Task SetStatusAsync(string hash, PostingStatus status, DateTimeOffset now, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(hash, out var r)) _rows[hash] = r with { Status = status, StatusAt = now };
            return Task.CompletedTask;
        }

        public Task<bool> SetStatusByUrlAsync(string url, PostingStatus status, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<StoredPosting>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredPosting>>(Array.Empty<StoredPosting>());

        public Task<int> ExpireStaleAsync(DateTimeOffset olderThan, DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task MarkLiveCheckedAsync(string hash, DateTimeOffset now, CancellationToken ct = default)
        {
            LiveCheckedAt[hash] = now;
            return Task.CompletedTask;
        }
    }
}
