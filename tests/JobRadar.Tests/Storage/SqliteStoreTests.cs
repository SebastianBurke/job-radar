using JobRadar.Core.Models;
using JobRadar.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Storage;

public sealed class SqliteStoreTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteStore _store = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"job-radar-test-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_dbPath, NullLogger<SqliteStore>.Instance);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static JobPosting MakePosting(string title = ".NET Engineer", string url = "https://example.com/jobs/1") =>
        new(
            Source: "test",
            Company: "Acme",
            Title: title,
            Location: "Remote",
            Url: url,
            Description: "We need a .NET engineer to do .NET things.");

    private static ScoringResult Sample() => new(
        MatchScore: 8,
        Eligibility: EligibilityVerdict.Eligible,
        EligibilityReason: "fits",
        Top3MatchedSkills: new[] { ".NET", "C#", "Azure" },
        TopConcern: "long ramp-up",
        EstimatedSeniority: "mid",
        LanguageRequired: "english",
        SalaryListed: "€60k",
        RemotePolicy: "remote",
        OneLinePitch: "Solid fit.");

    [Fact]
    public async Task Get_returns_null_for_unknown_hash()
    {
        Assert.Null(await _store.GetAsync("nope"));
    }

    [Fact]
    public async Task UpsertNew_then_Get_returns_pending_with_null_score()
    {
        var p = MakePosting();
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);

        var stored = await _store.GetAsync(p.Hash);
        Assert.NotNull(stored);
        Assert.Equal(PostingStatus.Pending, stored!.Status);
        Assert.Null(stored.CachedScore);
    }

    [Fact]
    public async Task UpsertNew_is_idempotent()
    {
        var p = MakePosting();
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow.AddHours(1));

        var stored = await _store.GetAsync(p.Hash);
        Assert.Equal(PostingStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task SaveScore_round_trips_ScoringResult()
    {
        var p = MakePosting();
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);
        var score = Sample();
        await _store.SaveScoreAsync(p.Hash, score, DateTimeOffset.UtcNow);

        var stored = await _store.GetAsync(p.Hash);
        Assert.NotNull(stored?.CachedScore);
        Assert.Equal(score.MatchScore, stored!.CachedScore!.MatchScore);
        Assert.Equal(score.Eligibility, stored.CachedScore.Eligibility);
        Assert.Equal(score.EligibilityReason, stored.CachedScore.EligibilityReason);
        Assert.NotNull(stored.CachedScore.Top3MatchedSkills);
        Assert.Equal(3, stored.CachedScore.Top3MatchedSkills!.Count);
        Assert.Equal(".NET", stored.CachedScore.Top3MatchedSkills[0]);
        Assert.Equal(score.TopConcern, stored.CachedScore.TopConcern);
        Assert.Equal(score.SalaryListed, stored.CachedScore.SalaryListed);
        Assert.Equal(score.RemotePolicy, stored.CachedScore.RemotePolicy);
        Assert.Equal(score.OneLinePitch, stored.CachedScore.OneLinePitch);
    }

    [Fact]
    public async Task SetStatusByUrl_matches_existing_url_and_returns_true()
    {
        var p = MakePosting(url: "https://example.com/jobs/match");
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);

        var hit = await _store.SetStatusByUrlAsync(p.Url, PostingStatus.Applied, DateTimeOffset.UtcNow);
        Assert.True(hit);

        var stored = await _store.GetAsync(p.Hash);
        Assert.Equal(PostingStatus.Applied, stored!.Status);
    }

    [Fact]
    public async Task SetStatusByUrl_returns_false_when_url_unknown()
    {
        var hit = await _store.SetStatusByUrlAsync("https://nope.example.com/x", PostingStatus.Applied, DateTimeOffset.UtcNow);
        Assert.False(hit);
    }

    [Fact]
    public async Task ListPending_excludes_applied_dismissed_expired()
    {
        var p1 = MakePosting(title: "A", url: "https://x/a");
        var p2 = MakePosting(title: "B", url: "https://x/b");
        var p3 = MakePosting(title: "C", url: "https://x/c");
        var p4 = MakePosting(title: "D", url: "https://x/d");
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p1, now);
        await _store.UpsertNewAsync(p2, now);
        await _store.UpsertNewAsync(p3, now);
        await _store.UpsertNewAsync(p4, now);
        await _store.SetStatusAsync(p2.Hash, PostingStatus.Applied, now);
        await _store.SetStatusAsync(p3.Hash, PostingStatus.Dismissed, now);
        await _store.SetStatusAsync(p4.Hash, PostingStatus.Expired, now);

        var pending = await _store.ListPendingAsync();
        Assert.Single(pending);
        Assert.Equal(p1.Hash, pending[0].Hash);
    }

    [Fact]
    public async Task ExpireStale_marks_only_pending_with_old_last_seen_at()
    {
        var p1 = MakePosting(title: "Stale", url: "https://x/stale");
        var p2 = MakePosting(title: "Fresh", url: "https://x/fresh");
        var p3 = MakePosting(title: "AppliedOld", url: "https://x/applied");
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p1, now);
        await _store.UpsertNewAsync(p2, now);
        await _store.UpsertNewAsync(p3, now);

        await _store.TouchLastSeenAsync(p1.Hash, now.AddDays(-30));
        await _store.TouchLastSeenAsync(p2.Hash, now);
        await _store.TouchLastSeenAsync(p3.Hash, now.AddDays(-30));
        await _store.SetStatusAsync(p3.Hash, PostingStatus.Applied, now);

        var expired = await _store.ExpireStaleAsync(now.AddDays(-7), now);

        Assert.Equal(1, expired);
        Assert.Equal(PostingStatus.Expired, (await _store.GetAsync(p1.Hash))!.Status);
        Assert.Equal(PostingStatus.Pending, (await _store.GetAsync(p2.Hash))!.Status);
        Assert.Equal(PostingStatus.Applied, (await _store.GetAsync(p3.Hash))!.Status);
    }

    [Fact]
    public async Task Migration_keeps_pre_v2_rows_pending_with_refreshed_last_seen_at()
    {
        // Tear down the auto-initialized store and replace with a hand-built v1 schema.
        await _store.DisposeAsync();
        File.Delete(_dbPath);

        await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE seen (
                    hash         TEXT PRIMARY KEY,
                    source       TEXT NOT NULL,
                    company      TEXT NOT NULL,
                    title        TEXT NOT NULL,
                    location     TEXT NOT NULL,
                    url          TEXT NOT NULL,
                    seen_at      TEXT NOT NULL
                );
                INSERT INTO seen (hash, source, company, title, location, url, seen_at)
                VALUES ('legacyhash', 'test', 'Acme', 'Old Role', 'Remote', 'https://x/old', '2026-01-01T00:00:00+00:00');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var beforeMigration = DateTimeOffset.UtcNow;
        _store = new SqliteStore(_dbPath, NullLogger<SqliteStore>.Instance);
        await _store.InitializeAsync();

        var stored = await _store.GetAsync("legacyhash");
        Assert.NotNull(stored);
        Assert.Equal(PostingStatus.Pending, stored!.Status);
        Assert.Null(stored.CachedScore);
        // last_seen_at must be refreshed to "now" so the 30-day grace clock starts today.
        Assert.True(stored.LastSeenAt >= beforeMigration);
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        var p = MakePosting();
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);

        // Re-initialize: the v2 sentinel must prevent the backfill from clobbering pending.
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        var stored = await _store.GetAsync(p.Hash);
        Assert.Equal(PostingStatus.Pending, stored!.Status);
    }

    [Fact]
    public async Task MarkLiveChecked_persists_timestamp_and_round_trips()
    {
        var p = MakePosting();
        await _store.UpsertNewAsync(p, DateTimeOffset.UtcNow);

        var checkedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store.MarkLiveCheckedAsync(p.Hash, checkedAt);

        var stored = await _store.GetAsync(p.Hash);
        Assert.NotNull(stored?.LiveCheckAt);
        Assert.Equal(checkedAt.UtcDateTime, stored!.LiveCheckAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Dead_status_round_trips_through_set_and_get()
    {
        var p = MakePosting();
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p, now);
        await _store.SetStatusAsync(p.Hash, PostingStatus.Dead, now);

        var stored = await _store.GetAsync(p.Hash);
        Assert.Equal(PostingStatus.Dead, stored!.Status);
    }

    [Fact]
    public async Task ScoringInputsHash_round_trips_through_save_score_and_get()
    {
        var p = MakePosting();
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p, now);
        await _store.SaveScoreAsync(p.Hash, Sample(), now, scoringInputsHash: "abc123def456");

        var stored = await _store.GetAsync(p.Hash);
        Assert.NotNull(stored);
        Assert.Equal("abc123def456", stored!.ScoringInputsHash);
    }

    [Fact]
    public async Task CountStaleCaches_returns_pending_rows_with_different_hash()
    {
        var matching = MakePosting(title: "Matching", url: "https://x/m");
        var stale = MakePosting(title: "Stale", url: "https://x/s");
        var unscored = MakePosting(title: "Unscored", url: "https://x/u");
        var applied = MakePosting(title: "Applied", url: "https://x/a");
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(matching, now);
        await _store.UpsertNewAsync(stale, now);
        await _store.UpsertNewAsync(unscored, now);
        await _store.UpsertNewAsync(applied, now);

        await _store.SaveScoreAsync(matching.Hash, Sample(), now, scoringInputsHash: "current");
        await _store.SaveScoreAsync(stale.Hash, Sample(), now, scoringInputsHash: "old");
        await _store.SaveScoreAsync(applied.Hash, Sample(), now, scoringInputsHash: "old");
        await _store.SetStatusAsync(applied.Hash, PostingStatus.Applied, now);
        // unscored: no SaveScoreAsync — stays NULL on both score_json and scoring_inputs_hash.

        var staleCount = await _store.CountStaleCachesAsync("current");

        Assert.Equal(1, staleCount); // only the pending row whose hash != "current" — unscored has no score_json, applied isn't pending
    }

    [Fact]
    public async Task CountStaleCaches_treats_null_hash_as_different_from_any_current_hash()
    {
        // Existing rows from before the migration carry NULL scoring_inputs_hash;
        // they should be invalidated automatically on the first run after the fix.
        var p = MakePosting();
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p, now);
        await _store.SaveScoreAsync(p.Hash, Sample(), now, scoringInputsHash: null);

        var staleCount = await _store.CountStaleCachesAsync("current-hash");
        Assert.Equal(1, staleCount);
    }

    [Fact]
    public async Task ListPending_excludes_dead()
    {
        var p1 = MakePosting(title: "Live", url: "https://x/live");
        var p2 = MakePosting(title: "Dead", url: "https://x/dead");
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertNewAsync(p1, now);
        await _store.UpsertNewAsync(p2, now);
        await _store.SetStatusAsync(p2.Hash, PostingStatus.Dead, now);

        var pending = await _store.ListPendingAsync();
        Assert.Single(pending);
        Assert.Equal(p1.Hash, pending[0].Hash);
    }

    [Fact]
    public void Hash_is_stable_for_same_posting_content()
    {
        var a = JobPosting.ComputeHash("Acme", "Engineer", "lorem ipsum");
        var b = JobPosting.ComputeHash("Acme", "Engineer", "lorem ipsum");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_differs_when_company_or_title_changes()
    {
        var basePost = JobPosting.ComputeHash("Acme", "Engineer", "lorem ipsum");
        Assert.NotEqual(basePost, JobPosting.ComputeHash("Acme2", "Engineer", "lorem ipsum"));
        Assert.NotEqual(basePost, JobPosting.ComputeHash("Acme", "Senior Engineer", "lorem ipsum"));
    }
}
