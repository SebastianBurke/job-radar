using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

/// <summary>
/// Reads JSON snapshots produced by an external capture process — typically a
/// sibling headless-browser instance running Playwright on the user's machine
/// (see HEADLESS-SOURCE-HANDOFF.md). Each file under
/// <c>data/captured/*.json</c> represents one source's latest scrape; the
/// adapter folds those postings into the same dedup → live-check → score
/// pipeline as the radar's first-party sources.
///
/// The schema each file must match is documented in HEADLESS-SOURCE-HANDOFF.md
/// and enforced here at parse time. Malformed files are logged and skipped;
/// missing directory is treated as "no captures yet" and the source emits
/// nothing (so the radar runs cleanly before dispatch ships its first file).
/// </summary>
public sealed class CapturedJsonSource : IJobSource
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _capturedDirectory;
    private readonly int _stalenessWarningDays;
    private readonly ILogger<CapturedJsonSource> _logger;

    public string Name => "captured";

    public CapturedJsonSource(string capturedDirectory, int stalenessWarningDays, ILogger<CapturedJsonSource> logger)
    {
        _capturedDirectory = capturedDirectory;
        _stalenessWarningDays = stalenessWarningDays;
        _logger = logger;
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_capturedDirectory))
        {
            _logger.LogInformation(
                "Captured directory {Dir} does not exist; no captured postings this run.",
                _capturedDirectory);
            yield break;
        }

        var files = Directory.GetFiles(_capturedDirectory, "*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            _logger.LogInformation("Captured directory {Dir} is empty; nothing to read.", _capturedDirectory);
            yield break;
        }

        var now = DateTimeOffset.UtcNow;
        var totalEmitted = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            CapturedFile? parsed;
            try
            {
                await using var stream = File.OpenRead(file);
                parsed = await JsonSerializer.DeserializeAsync<CapturedFile>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Captured file {File} failed to parse; skipping.", Path.GetFileName(file));
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Captured file {File} read failed; skipping.", Path.GetFileName(file));
                continue;
            }

            if (parsed is null)
            {
                _logger.LogWarning("Captured file {File} parsed to null; skipping.", Path.GetFileName(file));
                continue;
            }
            if (string.IsNullOrWhiteSpace(parsed.Source))
            {
                _logger.LogWarning("Captured file {File} missing required 'source' field; skipping.", Path.GetFileName(file));
                continue;
            }

            var ageDays = (now - parsed.CapturedAt).TotalDays;
            if (ageDays > _stalenessWarningDays)
            {
                _logger.LogWarning(
                    "Captured file {File} is {Age:F0} days old (>{Threshold}d staleness threshold); using anyway.",
                    Path.GetFileName(file), ageDays, _stalenessWarningDays);
            }

            var fileEmitted = 0;
            foreach (var posting in EmitPostings(parsed, file))
            {
                fileEmitted++;
                yield return posting;
            }
            totalEmitted += fileEmitted;
            _logger.LogInformation(
                "Captured {Source} from {File} (captured_at {CapturedAt:yyyy-MM-dd}): {Count} postings.",
                parsed.Source, Path.GetFileName(file), parsed.CapturedAt, fileEmitted);
        }

        _logger.LogInformation(
            "Captured-JSON adapter: {Files} files, {Total} postings total.", files.Length, totalEmitted);
    }

    private IEnumerable<JobPosting> EmitPostings(CapturedFile file, string filePath)
    {
        if (file.Postings is null)
        {
            yield break;
        }

        foreach (var p in file.Postings)
        {
            // Reject postings missing required fields rather than emitting half-blank
            // entries that would pollute the digest.
            if (string.IsNullOrWhiteSpace(p.Title)
                || string.IsNullOrWhiteSpace(p.Company)
                || string.IsNullOrWhiteSpace(p.Url)
                || string.IsNullOrWhiteSpace(p.Description))
            {
                _logger.LogWarning(
                    "Captured file {File} has a posting missing required fields (title/company/url/description); skipping that entry.",
                    Path.GetFileName(filePath));
                continue;
            }

            // Captured locations come from the underlying ATS's structured field,
            // so they're authoritative — same trust level as a Greenhouse / Lever
            // posting.
            yield return new JobPosting(
                Source: file.Source,
                Company: p.Company.Trim(),
                Title: p.Title.Trim(),
                Location: string.IsNullOrWhiteSpace(p.Location) ? "(unspecified)" : p.Location.Trim(),
                Url: p.Url.Trim(),
                Description: p.Description,
                PostedAt: p.PostedAt,
                Department: string.IsNullOrWhiteSpace(p.Department) ? null : p.Department,
                AtsId: string.IsNullOrWhiteSpace(p.AtsId) ? null : p.AtsId,
                LocationConfidence: LocationConfidence.Authoritative);
        }
    }

    /// <summary>Wire-format match for the schema in HEADLESS-SOURCE-HANDOFF.md.</summary>
    private sealed class CapturedFile
    {
        [JsonPropertyName("captured_at")] public DateTimeOffset CapturedAt { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("postings")] public List<CapturedPosting>? Postings { get; set; }
    }

    private sealed class CapturedPosting
    {
        [JsonPropertyName("ats_id")] public string? AtsId { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("company")] public string Company { get; set; } = string.Empty;
        [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("posted_at")] public DateTimeOffset? PostedAt { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
        // metadata is parsed but currently unused by the radar; held in case the
        // scorer's prompt wants to consume source-specific extras (classification,
        // language requirement, close date) in a follow-up.
        [JsonPropertyName("metadata")] public JsonElement? Metadata { get; set; }
    }
}
