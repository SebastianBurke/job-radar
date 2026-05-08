using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class WorkableSource : IJobSource
{
    private const string AtsKey = "workable";
    private const string Host = "apply.workable.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly CompaniesConfig _companies;
    private readonly ILogger<WorkableSource> _logger;

    public string Name => AtsKey;

    public WorkableSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        CompaniesConfig companies,
        ILogger<WorkableSource> logger)
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
            _logger.LogInformation("No Workable-tagged companies configured; skipping.");
            yield break;
        }

        using var http = _httpClientFactory.CreateJobRadarClient();

        foreach (var company in targets)
        {
            await _rateLimiter.WaitAsync(Host, ct);

            var url = $"https://{Host}/api/v1/widget/accounts/{company.Token}";
            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workable fetch failed for {Company}.", company.Name);
                response?.Dispose();
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            WorkableAccount? account;
            try
            {
                account = await JsonSerializer.DeserializeAsync<WorkableAccount>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Workable payload parse failed for {Company}.", company.Name);
                response.Dispose();
                continue;
            }
            response.Dispose();

            if (account?.Jobs is null) continue;

            var companyBlurb = HtmlText.Strip(account.Description);

            foreach (var j in account.Jobs)
            {
                if (string.IsNullOrWhiteSpace(j.Title) || string.IsNullOrWhiteSpace(j.Url)) continue;

                var locationParts = new[] { j.City, j.State, j.Country }.Where(s => !string.IsNullOrWhiteSpace(s));
                var location = string.Join(", ", locationParts);
                if (j.Telecommuting) location = string.IsNullOrEmpty(location) ? "Remote" : $"{location} (Remote)";
                if (string.IsNullOrEmpty(location)) location = "(unspecified)";

                // Workable widget API does not return per-job descriptions; combine job metadata + company
                // blurb so the keyword filter and scorer have something to chew on.
                var description = string.Join(" — ",
                    new[] { j.Title, j.Department, j.EmploymentType, j.Experience, companyBlurb }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                yield return new JobPosting(
                    Source: Name,
                    Company: company.Name,
                    Title: j.Title.Trim(),
                    Location: location,
                    Url: j.Url,
                    Description: description,
                    PostedAt: DateTimeOffset.TryParse(j.PublishedOn, out var dt) ? dt : null,
                    Department: j.Department,
                    AtsId: j.Shortcode,
                    AtsToken: company.Token,
                    LocationConfidence: LocationConfidence.Authoritative);
            }

            _logger.LogInformation("Workable {Company}: {Count} jobs.", company.Name, account.Jobs.Count);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class WorkableAccount
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("jobs")] public List<WorkableJob> Jobs { get; set; } = new();
    }

    private sealed class WorkableJob
    {
        [JsonPropertyName("shortcode")] public string? Shortcode { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
        [JsonPropertyName("employment_type")] public string? EmploymentType { get; set; }
        [JsonPropertyName("experience")] public string? Experience { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("telecommuting")] public bool Telecommuting { get; set; }
        [JsonPropertyName("published_on")] public string? PublishedOn { get; set; }
    }
}
