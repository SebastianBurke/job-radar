using JobRadar.Core.Models;
using JobRadar.Storage;
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

    [Fact]
    public async Task HasSeen_returns_false_for_unknown_hash()
    {
        Assert.False(await _store.HasSeenAsync("nope"));
    }

    [Fact]
    public async Task MarkSeen_then_HasSeen_returns_true()
    {
        var posting = new JobPosting(
            Source: "test",
            Company: "Acme",
            Title: ".NET Engineer",
            Location: "Remote",
            Url: "https://example.com/jobs/1",
            Description: "We need a .NET engineer to do .NET things.");
        var hash = posting.Hash;

        await _store.MarkSeenAsync(hash, posting, DateTimeOffset.UtcNow);

        Assert.True(await _store.HasSeenAsync(hash));
    }

    [Fact]
    public async Task MarkSeen_is_idempotent_on_same_hash()
    {
        var posting = new JobPosting(
            Source: "test",
            Company: "Acme",
            Title: "Backend",
            Location: "Madrid",
            Url: "https://example.com/2",
            Description: "Backend role.");

        await _store.MarkSeenAsync(posting.Hash, posting, DateTimeOffset.UtcNow);
        await _store.MarkSeenAsync(posting.Hash, posting, DateTimeOffset.UtcNow);

        Assert.True(await _store.HasSeenAsync(posting.Hash));
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
