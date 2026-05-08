using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources;

/// <summary>
/// Quebec's largest job board. Server-rendered HTML at jobillico.com/search-jobs;
/// each posting lands inside an <c>&lt;article data-job-url=... data-company-id=...&gt;</c>
/// block with title / company / location / salary / posted-date in stable inner markup.
///
/// The source iterates the cross product of <c>search_terms × locations</c> from
/// <c>config/sources.yml</c> and dedupes by Jobillico's per-job numeric id so the
/// same posting found via two different searches isn't emitted twice.
/// </summary>
public sealed class JobillicoSource : IJobSource
{
    private const string Host = "www.jobillico.com";
    private const string BaseUrl = "https://www.jobillico.com";

    // Each <article> opens with data-job-url + data-company-id; we capture the
    // path (which carries the job id) and the rest of the article body to mine
    // sub-fields out of. Singleline so '.' crosses newlines.
    private static readonly Regex ArticleRegex = new(
        @"<article\s[^>]*?data-job-url=""(?<urlPath>[^""]+)""[^>]*?data-company-id=""(?<companyId>\d+)""(?<rest>.*?)</article>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new(
        @"<h2\s[^>]*class=""h3[^""]*""[^>]*>\s*<a\s[^>]*>([^<]+)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CompanyRegex = new(
        @"<h3\s[^>]*class=""h4""[^>]*>\s*<a\s[^>]*class=""link companyLink[^""]*""[^>]*>([^<]+)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescriptionRegex = new(
        @"<p\s[^>]*class=""xs word-break""[^>]*>(.+?)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocationRegex = new(
        @"<span\s[^>]*icon--information--position[^>]*></span>\s*<p[^>]*>([^<]+)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SalaryRegex = new(
        @"<span\s[^>]*icon--information--money[^>]*></span>\s*<p[^>]*>(.+?)</p>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostedAtRegex = new(
        @"<time\s[^>]*datetime=""(\d{4}-\d{2}-\d{2})""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JobIdRegex = new(@"/(\d+)$", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<JobillicoSource> _logger;
    private readonly IReadOnlyList<string> _searchTerms;
    private readonly IReadOnlyList<string> _locations;

    public string Name => "jobillico";

    public JobillicoSource(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<JobillicoSource> logger,
        SourcesConfig sourcesConfig)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _searchTerms = sourcesConfig.Jobillico.SearchTerms.Count > 0
            ? sourcesConfig.Jobillico.SearchTerms
            : new[] { "software" };
        _locations = sourcesConfig.Jobillico.Locations.Count > 0
            ? sourcesConfig.Jobillico.Locations
            : new[] { "Outaouais", "Montreal" };
    }

    public async IAsyncEnumerable<JobPosting> FetchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateJobRadarClient();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in _searchTerms)
        {
            foreach (var loc in _locations)
            {
                await _rateLimiter.WaitAsync(Host, ct);
                var url = $"{BaseUrl}/search-jobs?skwd={Uri.EscapeDataString(term)}&sloc={Uri.EscapeDataString(loc)}&srdb=10&jobAge=14";

                string html;
                try
                {
                    using var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Jobillico {Term}/{Loc} -> HTTP {Status}; skipping this combination.",
                            term, loc, (int)resp.StatusCode);
                        continue;
                    }
                    html = await resp.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Jobillico fetch failed for {Term}/{Loc}.", term, loc);
                    continue;
                }

                var emitted = 0;
                foreach (var posting in Parse(html, seenIds))
                {
                    emitted++;
                    yield return posting;
                }

                _logger.LogInformation(
                    "Jobillico {Term} in {Loc}: {Count} new postings (after dedup against earlier searches).",
                    term, loc, emitted);
            }
        }
    }

    /// <summary>Public for tests — given a captured HTML body, yield postings.</summary>
    public static IEnumerable<JobPosting> Parse(string html, HashSet<string>? seenIds = null)
    {
        seenIds ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ArticleRegex.Matches(html))
        {
            var urlPath = m.Groups["urlPath"].Value;
            var rest = m.Groups["rest"].Value;

            // urlPath is "company-slug/title-slug/{id}?tracking=…". Strip query
            // string and grab the trailing numeric id.
            var pathBeforeQs = urlPath.Split('?', 2)[0];
            var idMatch = JobIdRegex.Match(pathBeforeQs);
            var atsId = idMatch.Success ? idMatch.Groups[1].Value : null;
            if (string.IsNullOrEmpty(atsId)) continue;
            if (!seenIds.Add(atsId)) continue;

            var title = WebUtility.HtmlDecode(TitleRegex.Match(rest).Groups[1].Value).Trim();
            var company = WebUtility.HtmlDecode(CompanyRegex.Match(rest).Groups[1].Value).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(company)) continue;

            var description = HtmlText.Strip(DescriptionRegex.Match(rest).Groups[1].Value);
            description = description.Replace("[...]", string.Empty).Trim();
            var location = NormalizeLocation(LocationRegex.Match(rest).Groups[1].Value.Trim());
            var salary = SalaryRegex.Match(rest).Groups[1].Value;
            salary = Regex.Replace(salary, @"\s+", " ").Trim();

            DateTimeOffset? postedAt = null;
            var postedAtMatch = PostedAtRegex.Match(rest);
            if (postedAtMatch.Success && DateTimeOffset.TryParse(postedAtMatch.Groups[1].Value, out var dt))
            {
                postedAt = dt;
            }

            // Salary lives outside Description in the model; fold it in here so
            // the scorer's prompt sees it without a model change.
            var fullDescription = string.IsNullOrEmpty(salary)
                ? description
                : $"{description}\n\nSalary: {salary}";

            yield return new JobPosting(
                Source: "jobillico",
                Company: company,
                Title: title,
                Location: string.IsNullOrEmpty(location) ? "(unspecified)" : location,
                Url: $"{BaseUrl}/en/job-offer/{pathBeforeQs}",
                Description: fullDescription,
                PostedAt: postedAt,
                AtsId: atsId,
                LocationConfidence: LocationConfidence.AggregatorOnly);
        }
    }

    /// <summary>
    /// Jobillico writes locations as "City - QC" / "City - ON" etc. The pipeline's
    /// <c>location_allow</c> filter has province names ("quebec", "ontario") on it
    /// but not the two-letter codes, so a literal "City - QC" string would be
    /// dropped before scoring. Expand the trailing code to the full province name
    /// + ", Canada" so existing filter configs keep working.
    /// </summary>
    private static string NormalizeLocation(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return raw
            .Replace(" - QC", ", Quebec, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - ON", ", Ontario, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - AB", ", Alberta, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - BC", ", British Columbia, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - NS", ", Nova Scotia, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - NB", ", New Brunswick, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - MB", ", Manitoba, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - SK", ", Saskatchewan, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - NL", ", Newfoundland and Labrador, Canada", StringComparison.OrdinalIgnoreCase)
            .Replace(" - PE", ", Prince Edward Island, Canada", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
