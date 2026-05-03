using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class RemoteOKSource : IJobSource
{
    private const string Host = "remoteok.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<RemoteOKSource> _logger;

    public string Name => "remoteok";

    public RemoteOKSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<RemoteOKSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await _rateLimiter.WaitAsync(Host, ct);

        using var http = _httpClientFactory.CreateJobRadarClient();
        var url = $"https://{Host}/api";

        HttpResponseMessage? response = null;
        try
        {
            response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoteOK fetch failed.");
            response?.Dispose();
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        List<RemoteOkEntry>? entries;
        try
        {
            entries = await JsonSerializer.DeserializeAsync<List<RemoteOkEntry>>(stream, JsonOpts, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "RemoteOK payload parse failed.");
            response.Dispose();
            yield break;
        }
        response.Dispose();

        if (entries is null) yield break;

        var jobs = 0;
        foreach (var e in entries)
        {
            // The first array element is a metadata/legal banner, not a job.
            if (string.IsNullOrWhiteSpace(e.Position) || string.IsNullOrWhiteSpace(e.Company)) continue;
            if (string.IsNullOrWhiteSpace(e.Url) && string.IsNullOrWhiteSpace(e.ApplyUrl)) continue;

            jobs++;
            yield return new JobPosting(
                Source: Name,
                Company: e.Company,
                Title: e.Position!,
                Location: string.IsNullOrWhiteSpace(e.Location) ? "Remote" : e.Location!,
                Url: !string.IsNullOrWhiteSpace(e.ApplyUrl) ? e.ApplyUrl! : e.Url!,
                Description: HtmlText.Strip(e.Description),
                PostedAt: e.Epoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Epoch) : null);
        }

        _logger.LogInformation("RemoteOK: {Count} jobs.", jobs);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class RemoteOkEntry
    {
        [JsonPropertyName("position")] public string? Position { get; set; }
        [JsonPropertyName("company")] public string? Company { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("apply_url")] public string? ApplyUrl { get; set; }
        [JsonPropertyName("epoch")] public long Epoch { get; set; }
    }
}
