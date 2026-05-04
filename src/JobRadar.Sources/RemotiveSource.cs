using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class RemotiveSource : IJobSource
{
    private const string Host = "remotive.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<RemotiveSource> _logger;
    private readonly IReadOnlyList<string> _searchTerms;

    public string Name => "remotive";

    public RemotiveSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<RemotiveSource> logger,
        SourcesConfig sourcesConfig)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _searchTerms = sourcesConfig.Remotive.SearchTerms.Count > 0
            ? sourcesConfig.Remotive.SearchTerms
            : throw new InvalidOperationException(
                "config/sources.yml is missing remotive.search_terms; add at least one term.");
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateJobRadarClient();
        var seen = new HashSet<long>();

        foreach (var term in _searchTerms)
        {
            await _rateLimiter.WaitAsync(Host, ct);
            var url = $"https://{Host}/api/remote-jobs?search={Uri.EscapeDataString(term)}";

            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remotive fetch failed for term {Term}.", term);
                response?.Dispose();
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            RemotiveListing? listing;
            try
            {
                listing = await JsonSerializer.DeserializeAsync<RemotiveListing>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Remotive payload parse failed for term {Term}.", term);
                response.Dispose();
                continue;
            }
            response.Dispose();

            if (listing?.Jobs is null) continue;

            var emitted = 0;
            foreach (var j in listing.Jobs)
            {
                if (string.IsNullOrWhiteSpace(j.Title) || string.IsNullOrWhiteSpace(j.Url)) continue;
                if (!seen.Add(j.Id)) continue;
                emitted++;
                yield return new JobPosting(
                    Source: Name,
                    Company: j.CompanyName ?? "(unknown)",
                    Title: j.Title.Trim(),
                    Location: j.CandidateRequiredLocation ?? "Remote",
                    Url: j.Url,
                    Description: HtmlText.Strip(j.Description),
                    PostedAt: DateTimeOffset.TryParse(j.PublicationDate, out var dt) ? dt : null,
                    Department: j.Category);
            }

            _logger.LogInformation("Remotive {Term}: {Count} new jobs (deduped against earlier terms).", term, emitted);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class RemotiveListing
    {
        [JsonPropertyName("jobs")] public List<RemotiveJob> Jobs { get; set; } = new();
    }

    private sealed class RemotiveJob
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("company_name")] public string? CompanyName { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("candidate_required_location")] public string? CandidateRequiredLocation { get; set; }
        [JsonPropertyName("publication_date")] public string? PublicationDate { get; set; }
    }
}
