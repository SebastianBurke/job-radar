using System.Text.Json;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JobRadar.Storage;

public sealed class SqliteStore : IDedupStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions ScoreJsonOptions = new()
    {
        WriteIndented = false,
    };

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

        // Phase 1: ensure the table exists with the v2 shape (fresh installs land here).
        // For pre-existing v1 tables this is a no-op; phase 2 adds the missing columns.
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS seen (
                    hash         TEXT PRIMARY KEY,
                    source       TEXT NOT NULL,
                    company      TEXT NOT NULL,
                    title        TEXT NOT NULL,
                    location     TEXT NOT NULL,
                    url          TEXT NOT NULL,
                    seen_at      TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL DEFAULT '',
                    status       TEXT NOT NULL DEFAULT 'pending',
                    status_at    TEXT,
                    score_json   TEXT
                );
                CREATE TABLE IF NOT EXISTS schema_meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_seen_seen_at ON seen(seen_at);
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        // Phase 2: add columns missing from a pre-v2 schema.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(seen);";
            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        async Task AddColumnIfMissing(string column, string ddlSuffix)
        {
            if (existingColumns.Contains(column)) return;
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE seen ADD COLUMN {column} {ddlSuffix};";
            await alter.ExecuteNonQueryAsync(ct);
            existingColumns.Add(column);
        }

        await AddColumnIfMissing("last_seen_at", "TEXT NOT NULL DEFAULT ''");
        await AddColumnIfMissing("status", "TEXT NOT NULL DEFAULT 'pending'");
        await AddColumnIfMissing("status_at", "TEXT");
        await AddColumnIfMissing("score_json", "TEXT");
        await AddColumnIfMissing("live_check_at", "TEXT");
        await AddColumnIfMissing("scoring_inputs_hash", "TEXT");

        // Phase 3: indexes that reference v2-only columns. Safe now that the columns exist.
        await using (var indexes = conn.CreateCommand())
        {
            indexes.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_seen_status ON seen(status);
                CREATE INDEX IF NOT EXISTS idx_seen_url ON seen(url);
                """;
            await indexes.ExecuteNonQueryAsync(ct);
        }

        // One-time backfill: pre-v2 rows have no last_seen_at. Refresh it to "now" so the
        // 30-day grace clock starts today instead of treating them as instantly stale. They
        // stay pending; the pipeline re-scores any whose feed still lists them (cached score
        // is NULL until the next encounter), and pending_grace_days handles the rest.
        await using (var sentinel = conn.CreateCommand())
        {
            sentinel.CommandText = "SELECT value FROM schema_meta WHERE key = 'migrated_v2' LIMIT 1;";
            var existing = await sentinel.ExecuteScalarAsync(ct);
            if (existing is null)
            {
                var nowIso = DateTimeOffset.UtcNow.ToString("O");
                await using (var backfill = conn.CreateCommand())
                {
                    backfill.CommandText = """
                        UPDATE seen
                           SET last_seen_at = $now
                         WHERE last_seen_at = '';
                        """;
                    backfill.Parameters.AddWithValue("$now", nowIso);
                    var affected = await backfill.ExecuteNonQueryAsync(ct);
                    if (affected > 0)
                    {
                        _logger.LogInformation("Schema migration v2: initialized last_seen_at on {Count} pre-existing rows; treating today as day 1.", affected);
                    }
                }

                await using var mark = conn.CreateCommand();
                mark.CommandText = "INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('migrated_v2', '1');";
                await mark.ExecuteNonQueryAsync(ct);
            }
        }

        _logger.LogDebug("SqliteStore initialized at {ConnectionString}", _connectionString);
    }

    public async Task<StoredPosting?> GetAsync(string hash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT hash, source, company, title, location, url, seen_at, last_seen_at, status, status_at, score_json, live_check_at, scoring_inputs_hash
              FROM seen
             WHERE hash = $hash
             LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$hash", hash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task UpsertNewAsync(JobPosting posting, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO seen
                (hash, source, company, title, location, url, seen_at, last_seen_at, status, status_at, score_json)
            VALUES
                ($hash, $source, $company, $title, $location, $url, $now, $now, 'pending', $now, NULL);
            """;
        cmd.Parameters.AddWithValue("$hash", posting.Hash);
        cmd.Parameters.AddWithValue("$source", posting.Source);
        cmd.Parameters.AddWithValue("$company", posting.Company);
        cmd.Parameters.AddWithValue("$title", posting.Title);
        cmd.Parameters.AddWithValue("$location", posting.Location);
        cmd.Parameters.AddWithValue("$url", posting.Url);
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task TouchLastSeenAsync(string hash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE seen SET last_seen_at = $now WHERE hash = $hash;";
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveScoreAsync(string hash, ScoringResult score, DateTimeOffset now, string? scoringInputsHash = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE seen
               SET score_json = $score,
                   last_seen_at = $now,
                   scoring_inputs_hash = $inputs_hash
             WHERE hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$score", JsonSerializer.Serialize(score, ScoreJsonOptions));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$inputs_hash", (object?)scoringInputsHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountStaleCachesAsync(string currentScoringInputsHash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM seen
             WHERE score_json IS NOT NULL
               AND status = 'pending'
               AND COALESCE(scoring_inputs_hash, '') != $current;
            """;
        cmd.Parameters.AddWithValue("$current", currentScoringInputsHash ?? string.Empty);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long n ? (int)n : 0;
    }

    public async Task SetStatusAsync(string hash, PostingStatus status, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE seen
               SET status = $status,
                   status_at = $now
             WHERE hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$status", StatusToText(status));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetStatusByUrlAsync(string url, PostingStatus status, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE seen
               SET status = $status,
                   status_at = $now
             WHERE url = $url;
            """;
        cmd.Parameters.AddWithValue("$status", StatusToText(status));
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$url", url);
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task MarkLiveCheckedAsync(string hash, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE seen
               SET live_check_at = $now
             WHERE hash = $hash;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StoredPosting>> ListPendingAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT hash, source, company, title, location, url, seen_at, last_seen_at, status, status_at, score_json, live_check_at, scoring_inputs_hash
              FROM seen
             WHERE status = 'pending';
            """;
        var results = new List<StoredPosting>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Read(reader));
        }
        return results;
    }

    public async Task<int> ExpireStaleAsync(DateTimeOffset olderThan, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE seen
               SET status = 'expired',
                   status_at = $now
             WHERE status = 'pending'
               AND last_seen_at < $cutoff;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static StoredPosting Read(SqliteDataReader r)
    {
        var hash = r.GetString(0);
        var source = r.GetString(1);
        var company = r.GetString(2);
        var title = r.GetString(3);
        var location = r.GetString(4);
        var url = r.GetString(5);
        var seenAt = DateTimeOffset.Parse(r.GetString(6));
        var lastSeenAtRaw = r.GetString(7);
        var lastSeenAt = string.IsNullOrEmpty(lastSeenAtRaw) ? seenAt : DateTimeOffset.Parse(lastSeenAtRaw);
        var status = ParseStatus(r.GetString(8));
        DateTimeOffset? statusAt = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9));
        ScoringResult? cached = r.IsDBNull(10) ? null : DeserializeScore(r.GetString(10));
        DateTimeOffset? liveCheckAt = r.FieldCount > 11 && !r.IsDBNull(11)
            ? DateTimeOffset.Parse(r.GetString(11))
            : null;
        string? scoringInputsHash = r.FieldCount > 12 && !r.IsDBNull(12)
            ? r.GetString(12)
            : null;

        // The original description is not persisted; carry-overs reconstruct without it.
        // Renderer never reads description, and the hash is stable from the original encounter.
        var posting = new JobPosting(source, company, title, location, url, Description: string.Empty);
        return new StoredPosting(hash, posting, status, cached, seenAt, lastSeenAt, statusAt, liveCheckAt, scoringInputsHash);
    }

    private static ScoringResult? DeserializeScore(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScoringResult>(json, ScoreJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StatusToText(PostingStatus s) => s switch
    {
        PostingStatus.Pending => "pending",
        PostingStatus.Applied => "applied",
        PostingStatus.Dismissed => "dismissed",
        PostingStatus.Expired => "expired",
        PostingStatus.Dead => "dead",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unknown status"),
    };

    private static PostingStatus ParseStatus(string text) => text switch
    {
        "pending" => PostingStatus.Pending,
        "applied" => PostingStatus.Applied,
        "dismissed" => PostingStatus.Dismissed,
        "expired" => PostingStatus.Expired,
        "dead" => PostingStatus.Dead,
        _ => PostingStatus.Pending,
    };
}
