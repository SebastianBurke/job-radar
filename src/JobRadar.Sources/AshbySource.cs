using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class AshbySource : IJobSource
{
    private const string AtsKey = "ashby";
    private const string Host = "api.ashbyhq.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly CompaniesConfig _companies;
    private readonly ILogger<AshbySource> _logger;

    public string Name => AtsKey;

    public AshbySource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        CompaniesConfig companies,
        ILogger<AshbySource> logger)
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
            _logger.LogInformation("No Ashby-tagged companies configured; skipping.");
            yield break;
        }

        using var http = _httpClientFactory.CreateJobRadarClient();

        foreach (var company in targets)
        {
            await _rateLimiter.WaitAsync(Host, ct);

            var url = $"https://{Host}/posting-api/job-board/{company.Token}";
            HttpResponseMessage? response = null;
            try
            {
                response = await http.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ashby fetch failed for {Company}.", company.Name);
                response?.Dispose();
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            AshbyListing? listing;
            try
            {
                listing = await JsonSerializer.DeserializeAsync<AshbyListing>(stream, JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ashby payload parse failed for {Company}.", company.Name);
                response.Dispose();
                continue;
            }
            response.Dispose();

            if (listing?.Jobs is null) continue;

            foreach (var j in listing.Jobs)
            {
                if (string.IsNullOrWhiteSpace(j.Title) || string.IsNullOrWhiteSpace(j.JobUrl)) continue;
                var description = !string.IsNullOrWhiteSpace(j.DescriptionPlain)
                    ? j.DescriptionPlain
                    : HtmlText.Strip(j.DescriptionHtml);
                var location = !string.IsNullOrWhiteSpace(j.Location)
                    ? j.Location!
                    : (j.IsRemote == true ? "Remote" : "(unspecified)");
                yield return new JobPosting(
                    Source: Name,
                    Company: company.Name,
                    Title: j.Title.Trim(),
                    Location: location,
                    Url: j.JobUrl,
                    Description: description ?? string.Empty,
                    PostedAt: j.PublishedAt,
                    Department: j.Department,
                    AtsId: j.Id,
                    AtsToken: company.Token);
            }

            _logger.LogInformation("Ashby {Company}: {Count} jobs.", company.Name, listing.Jobs.Count);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class AshbyListing
    {
        [JsonPropertyName("jobs")] public List<AshbyJob> Jobs { get; set; } = new();
    }

    private sealed class AshbyJob
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("jobUrl")] public string? JobUrl { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
        [JsonPropertyName("descriptionHtml")] public string? DescriptionHtml { get; set; }
        [JsonPropertyName("descriptionPlain")] public string? DescriptionPlain { get; set; }
        [JsonPropertyName("publishedAt")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("isRemote")]
        [JsonConverter(typeof(BoolStringConverter))]
        public bool? IsRemote { get; set; }
    }

    private sealed class BoolStringConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => bool.TryParse(reader.GetString(), out var b) ? b : null,
                JsonTokenType.Null => null,
                _ => null,
            };

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteBooleanValue(value.Value);
            else writer.WriteNullValue();
        }
    }
}
