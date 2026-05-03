using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class HackerNewsHiringSource : IJobSource
{
    private const string AlgoliaHost = "hn.algolia.com";
    private const string FirebaseHost = "hacker-news.firebaseio.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<HackerNewsHiringSource> _logger;

    public string Name => "hackernews";

    public HackerNewsHiringSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<HackerNewsHiringSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateJobRadarClient();

        long? threadId = null;
        await _rateLimiter.WaitAsync(AlgoliaHost, ct);
        try
        {
            var searchUrl = $"https://{AlgoliaHost}/api/v1/search_by_date?query=%22Who+is+hiring%22&tags=story,author_whoishiring&hitsPerPage=1";
            var search = await http.GetFromJsonAsync<AlgoliaSearch>(searchUrl, JsonOpts, ct);
            threadId = search?.Hits?.FirstOrDefault()?.ObjectId switch
            {
                { } id when long.TryParse(id, out var n) => n,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HN Algolia search failed.");
        }

        if (threadId is null)
        {
            _logger.LogInformation("HN: no current 'Who is hiring' thread found; skipping.");
            yield break;
        }

        await _rateLimiter.WaitAsync(FirebaseHost, ct);
        HnItem? thread;
        try
        {
            thread = await http.GetFromJsonAsync<HnItem>($"https://{FirebaseHost}/v0/item/{threadId}.json", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HN thread fetch failed for {ThreadId}.", threadId);
            yield break;
        }

        if (thread?.Kids is null || thread.Kids.Count == 0)
        {
            _logger.LogInformation("HN thread {ThreadId} had no comments.", threadId);
            yield break;
        }

        var emitted = 0;
        foreach (var kidId in thread.Kids)
        {
            await _rateLimiter.WaitAsync(FirebaseHost, ct);
            HnItem? comment;
            try
            {
                comment = await http.GetFromJsonAsync<HnItem>($"https://{FirebaseHost}/v0/item/{kidId}.json", JsonOpts, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HN comment {Id} fetch failed.", kidId);
                continue;
            }

            if (comment is null || comment.Deleted == true || comment.Dead == true) continue;
            if (string.IsNullOrWhiteSpace(comment.Text)) continue;

            var clean = HtmlText.Strip(comment.Text);
            var (title, location) = ExtractTitleAndLocation(clean);

            emitted++;
            yield return new JobPosting(
                Source: Name,
                Company: ExtractCompany(clean),
                Title: title,
                Location: location,
                Url: $"https://news.ycombinator.com/item?id={kidId}",
                Description: clean,
                PostedAt: comment.Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(comment.Time) : null);
        }

        _logger.LogInformation("HN whoishiring thread {ThreadId}: emitted {Count} comments as postings.", threadId, emitted);
    }

    private static string ExtractCompany(string text)
    {
        var first = text.Split('|')[0].Trim();
        return string.IsNullOrEmpty(first) ? "(unknown)" : first;
    }

    private static (string Title, string Location) ExtractTitleAndLocation(string text)
    {
        var parts = text.Split('|', StringSplitOptions.TrimEntries);
        // Common HN format: COMPANY | ROLE | LOCATION | (rest)
        var role = parts.Length > 1 ? parts[1] : "Software role (HN)";
        var loc = parts.Length > 2 ? parts[2] : "(unspecified)";
        return (role, loc);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class AlgoliaSearch
    {
        [JsonPropertyName("hits")] public List<AlgoliaHit>? Hits { get; set; }
    }

    private sealed class AlgoliaHit
    {
        [JsonPropertyName("objectID")] public string? ObjectId { get; set; }
    }

    private sealed class HnItem
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("kids")] public List<long>? Kids { get; set; }
        [JsonPropertyName("time")] public long Time { get; set; }
        [JsonPropertyName("deleted")] public bool? Deleted { get; set; }
        [JsonPropertyName("dead")] public bool? Dead { get; set; }
    }
}
