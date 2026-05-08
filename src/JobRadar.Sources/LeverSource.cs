using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class LeverSource : IJobSource
{
    private const string AtsKey = "lever";
    private const string Host = "api.lever.co";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly CompaniesConfig _companies;
    private readonly ILogger<LeverSource> _logger;

    public string Name => AtsKey;

    public LeverSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        CompaniesConfig companies,
        ILogger<LeverSource> logger)
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
                        && !string.IsNullOrWhiteSpace(c.Token) && c.Token != "?")
            .ToList();

        if (targets.Count == 0)
        {
            _logger.LogInformation("No Lever-tagged companies configured; skipping.");
            yield break;
        }

        using var http = _httpClientFactory.CreateJobRadarClient();

        foreach (var company in targets)
        {
            await _rateLimiter.WaitAsync(Host, ct);

            var url = $"https://{Host}/v0/postings/{company.Token}?mode=json";
            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Lever 429 for {Company}; backing off 5s.", company.Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lever fetch failed for {Company} ({Token}).", company.Name, company.Token);
                response?.Dispose();
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            List<LeverPosting>? items;
            try
            {
                items = await JsonSerializer.DeserializeAsync<List<LeverPosting>>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Lever payload parse failed for {Company}.", company.Name);
                response.Dispose();
                continue;
            }
            response.Dispose();

            if (items is null) continue;

            foreach (var p in items)
            {
                if (string.IsNullOrWhiteSpace(p.Text) || string.IsNullOrWhiteSpace(p.HostedUrl)) continue;
                var location = p.Categories?.Location ?? p.Country ?? "(unspecified)";
                var description = !string.IsNullOrWhiteSpace(p.DescriptionPlain)
                    ? p.DescriptionPlain
                    : HtmlText.Strip(p.Description);
                yield return new JobPosting(
                    Source: Name,
                    Company: company.Name,
                    Title: p.Text.Trim(),
                    Location: location,
                    Url: p.HostedUrl,
                    Description: description ?? string.Empty,
                    PostedAt: p.CreatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.CreatedAt) : null,
                    Department: p.Categories?.Department,
                    AtsId: p.Id,
                    AtsToken: company.Token);
            }

            _logger.LogInformation("Lever {Company}: {Count} jobs.", company.Name, items.Count);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class LeverPosting
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("hostedUrl")] public string? HostedUrl { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
        [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("categories")] public LeverCategories? Categories { get; set; }
    }

    private sealed class LeverCategories
    {
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
    }
}
