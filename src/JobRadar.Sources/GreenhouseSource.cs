using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class GreenhouseSource : IJobSource
{
    private const string AtsKey = "greenhouse";
    private const string Host = "boards-api.greenhouse.io";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly CompaniesConfig _companies;
    private readonly ILogger<GreenhouseSource> _logger;

    public string Name => AtsKey;

    public GreenhouseSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        CompaniesConfig companies,
        ILogger<GreenhouseSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _companies = companies;
        _logger = logger;
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var targets = _companies.Companies
            .Where(c => string.Equals(c.Ats, AtsKey, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(c.Token)
                        && c.Token != "?")
            .ToList();

        if (targets.Count == 0)
        {
            _logger.LogInformation("No Greenhouse-tagged companies configured; skipping.");
            yield break;
        }

        using var http = _httpClientFactory.CreateJobRadarClient();

        foreach (var company in targets)
        {
            await _rateLimiter.WaitAsync(Host, ct);

            var url = $"https://{Host}/v1/boards/{company.Token}/jobs?content=true";
            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Greenhouse 429 for {Company}; backing off 5s.", company.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Greenhouse fetch failed for {Company} ({Token}).", company.Name, company.Token);
                response?.Dispose();
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            GreenhouseListing? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<GreenhouseListing>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Greenhouse payload parse failed for {Company}.", company.Name);
                response.Dispose();
                continue;
            }
            response.Dispose();

            if (payload?.Jobs is null) continue;

            foreach (var job in payload.Jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Title) || string.IsNullOrWhiteSpace(job.AbsoluteUrl)) continue;
                yield return new JobPosting(
                    Source: Name,
                    Company: company.Name,
                    Title: job.Title.Trim(),
                    Location: job.Location?.Name?.Trim() ?? "(unspecified)",
                    Url: job.AbsoluteUrl,
                    Description: HtmlText.Strip(job.Content),
                    PostedAt: job.UpdatedAt,
                    Department: job.Departments?.FirstOrDefault()?.Name);
            }

            _logger.LogInformation("Greenhouse {Company}: {Count} jobs.", company.Name, payload.Jobs.Count);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GreenhouseListing
    {
        [JsonPropertyName("jobs")]
        public List<GreenhouseJob> Jobs { get; set; } = new();
    }

    private sealed class GreenhouseJob
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("absolute_url")]
        public string? AbsoluteUrl { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("location")]
        public GreenhouseLocation? Location { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("departments")]
        public List<GreenhouseDepartment>? Departments { get; set; }
    }

    private sealed class GreenhouseLocation
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class GreenhouseDepartment
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
