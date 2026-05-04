using System.Runtime.CompilerServices;
using System.ServiceModel.Syndication;
using System.Xml;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

public sealed class WeWorkRemotelySource : IJobSource
{
    private const string Host = "weworkremotely.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<WeWorkRemotelySource> _logger;
    private readonly IReadOnlyList<string> _feeds;

    public string Name => "weworkremotely";

    public WeWorkRemotelySource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<WeWorkRemotelySource> logger,
        SourcesConfig sourcesConfig)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _feeds = sourcesConfig.WeWorkRemotely.Feeds.Count > 0
            ? sourcesConfig.WeWorkRemotely.Feeds
            : throw new InvalidOperationException(
                "config/sources.yml is missing weworkremotely.feeds; add at least one feed URL.");
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateJobRadarClient();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in _feeds)
        {
            await _rateLimiter.WaitAsync(Host, ct);

            string xml;
            try
            {
                xml = await http.GetStringAsync(feed, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WWR fetch failed for {Feed}.", feed);
                continue;
            }

            SyndicationFeed parsed;
            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                parsed = SyndicationFeed.Load(reader);
            }
            catch (XmlException ex)
            {
                _logger.LogWarning(ex, "WWR feed parse failed for {Feed}.", feed);
                continue;
            }

            var emitted = 0;
            foreach (var item in parsed.Items)
            {
                var url = item.Links.FirstOrDefault()?.Uri?.ToString();
                var rawTitle = item.Title?.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(url)) continue;
                if (!seen.Add(url)) continue;

                // WWR titles look like "Acme Inc: Senior Full Stack Developer"
                var (company, title) = SplitCompanyTitle(rawTitle);
                var description = HtmlText.Strip((item.Summary?.Text) ?? string.Empty);

                emitted++;
                yield return new JobPosting(
                    Source: Name,
                    Company: company,
                    Title: title,
                    Location: GuessLocation(description),
                    Url: url,
                    Description: description,
                    PostedAt: item.PublishDate == default ? null : item.PublishDate);
            }

            _logger.LogInformation("WWR {Feed}: {Count} jobs.", feed, emitted);
        }
    }

    private static (string Company, string Title) SplitCompanyTitle(string raw)
    {
        var idx = raw.IndexOf(':');
        if (idx <= 0 || idx >= raw.Length - 1) return ("(unknown)", raw.Trim());
        return (raw[..idx].Trim(), raw[(idx + 1)..].Trim());
    }

    private static string GuessLocation(string description)
    {
        // WWR descriptions usually contain a region tag like "Anywhere in the World" or "Europe Only".
        if (description.Contains("Anywhere in the World", StringComparison.OrdinalIgnoreCase)) return "Anywhere in the World";
        if (description.Contains("Europe Only", StringComparison.OrdinalIgnoreCase)) return "Europe";
        if (description.Contains("USA Only", StringComparison.OrdinalIgnoreCase)) return "USA Only";
        if (description.Contains("Canada Only", StringComparison.OrdinalIgnoreCase)) return "Canada";
        return "Remote";
    }
}
