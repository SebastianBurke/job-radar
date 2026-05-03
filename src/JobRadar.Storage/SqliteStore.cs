using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JobRadar.Storage;

public sealed class SqliteStore : IDedupStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteStore> _logger;

    public SqliteStore(string dbPath, ILogger<SqliteStore> logger)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS seen (
                hash         TEXT PRIMARY KEY,
                source       TEXT NOT NULL,
                company      TEXT NOT NULL,
                title        TEXT NOT NULL,
                location     TEXT NOT NULL,
                url          TEXT NOT NULL,
                seen_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_seen_seen_at ON seen(seen_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug("SqliteStore initialized at {ConnectionString}", _connectionString);
    }

    public async Task<bool> HasSeenAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM seen WHERE hash = $hash LIMIT 1;";
        cmd.Parameters.AddWithValue("$hash", hash);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task MarkSeenAsync(string hash, JobPosting posting, DateTimeOffset seenAt, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO seen (hash, source, company, title, location, url, seen_at)
            VALUES ($hash, $source, $company, $title, $location, $url, $seen_at);
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$source", posting.Source);
        cmd.Parameters.AddWithValue("$company", posting.Company);
        cmd.Parameters.AddWithValue("$title", posting.Title);
        cmd.Parameters.AddWithValue("$location", posting.Location);
        cmd.Parameters.AddWithValue("$url", posting.Url);
        cmd.Parameters.AddWithValue("$seen_at", seenAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
