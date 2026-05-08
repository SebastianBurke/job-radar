using System.Net;
using System.Text.RegularExpressions;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using JobRadar.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace JobRadar.Sources.LiveCheck;

/// <summary>
/// Verifies a posting is still listed on its underlying ATS (or, for aggregator
/// sources, that the apply URL still resolves). Dispatches per <see cref="JobPosting.Source"/>.
/// </summary>
public sealed class AtsLiveChecker : IAtsLiveChecker
{
    private static readonly Regex GreenhouseUrl = new(
        @"boards(?:[.\-][a-z]+)?\.greenhouse\.io/(?<token>[^/?#]+)/jobs/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeverUrl = new(
        @"jobs\.lever\.co/(?<company>[^/?#]+)/(?<id>[A-Za-z0-9\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AshbyUrl = new(
        @"jobs\.ashbyhq\.com/(?<org>[^/?#]+)/(?<id>[A-Za-z0-9\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WorkableUrl = new(
        @"apply\.workable\.com/(?<account>[^/?#]+)/j/(?<id>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DeadBodyMarkers = new[]
    {
        "no longer available",
        "this position has been filled",
        "this job is closed",
        "this opening has been filled",
        "we are no longer accepting applications",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<AtsLiveChecker> _logger;

    public AtsLiveChecker(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<AtsLiveChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<LiveCheckResult> CheckAsync(JobPosting posting, LiveCheckMode mode, CancellationToken ct = default)
    {
        if (mode == LiveCheckMode.None)
        {
            return LiveCheckResult.Skipped();
        }

        try
        {
            return (posting.Source ?? string.Empty).ToLowerInvariant() switch
            {
                "greenhouse" => await CheckGreenhouseAsync(posting, ct),
                "lever" => await CheckLeverAsync(posting, ct),
                "ashby" => await CheckAshbyAsync(posting, ct),
                "workable" => await CheckWorkableAsync(posting, ct),
                _ => await CheckAggregatorAsync(posting, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LiveCheckResult.Unknown($"exception: {ex.GetType().Name} {ex.Message}");
        }
    }

    private async Task<LiveCheckResult> CheckGreenhouseAsync(JobPosting posting, CancellationToken ct)
    {
        var (token, id) = ResolveTokenAndId(posting, GreenhouseUrl, "token", "id");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(id))
        {
            return LiveCheckResult.Unknown("greenhouse: missing token/id in posting and URL");
        }

        var apiUrl = $"https://boards-api.greenhouse.io/v1/boards/{token}/jobs/{id}";
        return await ApiGetAsync("boards-api.greenhouse.io", apiUrl, requireBody: true, ct);
    }

    private async Task<LiveCheckResult> CheckLeverAsync(JobPosting posting, CancellationToken ct)
    {
        var (company, id) = ResolveTokenAndId(posting, LeverUrl, "company", "id");
        if (string.IsNullOrEmpty(company) || string.IsNullOrEmpty(id))
        {
            return LiveCheckResult.Unknown("lever: missing company/id in posting and URL");
        }

        var apiUrl = $"https://api.lever.co/v0/postings/{company}/{id}";
        return await ApiGetAsync("api.lever.co", apiUrl, requireBody: true, ct);
    }

    private async Task<LiveCheckResult> CheckAshbyAsync(JobPosting posting, CancellationToken ct)
    {
        // Ashby's posting-api is org-scoped (no per-job endpoint). The public job page
        // is the cleanest 200/404 signal: jobs.ashbyhq.com/{org}/{id}.
        var match = string.IsNullOrEmpty(posting.Url) ? null : AshbyUrl.Match(posting.Url);
        if (match is null || !match.Success)
        {
            return LiveCheckResult.Unknown("ashby: URL does not match jobs.ashbyhq.com pattern");
        }

        return await ApiGetAsync("jobs.ashbyhq.com", posting.Url, requireBody: false, ct);
    }

    private async Task<LiveCheckResult> CheckWorkableAsync(JobPosting posting, CancellationToken ct)
    {
        // Workable's public URL is the most reliable per-job liveness signal:
        // apply.workable.com/{account}/j/{shortcode}.
        var match = string.IsNullOrEmpty(posting.Url) ? null : WorkableUrl.Match(posting.Url);
        if (match is null || !match.Success)
        {
            return LiveCheckResult.Unknown("workable: URL does not match apply.workable.com pattern");
        }

        return await ApiGetAsync("apply.workable.com", posting.Url, requireBody: false, ct);
    }

    private async Task<LiveCheckResult> CheckAggregatorAsync(JobPosting posting, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(posting.Url))
        {
            return LiveCheckResult.Unknown("aggregator: empty URL");
        }

        if (!Uri.TryCreate(posting.Url, UriKind.Absolute, out var uri))
        {
            return LiveCheckResult.Unknown($"aggregator: invalid URL '{posting.Url}'");
        }

        await _rateLimiter.WaitAsync(uri.Host, ct);
        using var client = _httpClientFactory.CreateJobRadarClient();
        // GET with default redirect handling, but cap body read so a giant page doesn't bloat us.
        using var resp = await client.GetAsync(posting.Url, HttpCompletionOption.ResponseContentRead, ct);
        var status = (int)resp.StatusCode;
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? posting.Url;

        if (status == 404 || status == 410)
        {
            return LiveCheckResult.Dead($"GET {posting.Url} -> {status}");
        }

        // Login wall heuristic: redirected somewhere that screams "auth".
        if (LooksLikeLoginWall(finalUrl))
        {
            return LiveCheckResult.Dead($"redirected to login wall: {finalUrl}");
        }

        if (status >= 200 && status < 400)
        {
            // Best-effort body inspection: pull at most ~32 KB so we don't waste time on
            // the whole HTML page. The killed-posting markers are typically near the top.
            string body;
            try
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[32 * 1024];
                var read = await stream.ReadAsync(buffer.AsMemory(), ct);
                body = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch
            {
                body = string.Empty;
            }

            foreach (var marker in DeadBodyMarkers)
            {
                if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return LiveCheckResult.Dead($"page body matched '{marker}'");
                }
            }

            return LiveCheckResult.Live($"GET {posting.Url} -> {status}");
        }

        return LiveCheckResult.Unknown($"GET {posting.Url} -> {status}");
    }

    private async Task<LiveCheckResult> ApiGetAsync(string host, string url, bool requireBody, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(host, ct);
        using var client = _httpClientFactory.CreateJobRadarClient();
        using var resp = await client.GetAsync(url, ct);
        var status = (int)resp.StatusCode;

        if (status == 404 || status == 410)
        {
            return LiveCheckResult.Dead($"GET {url} -> {status}");
        }

        if (status >= 200 && status < 300)
        {
            if (!requireBody) return LiveCheckResult.Live($"GET {url} -> {status}");

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0
                ? LiveCheckResult.Live($"GET {url} -> {status} ({bytes.Length} bytes)")
                : LiveCheckResult.Dead($"GET {url} -> {status} but empty body");
        }

        return LiveCheckResult.Unknown($"GET {url} -> {status}");
    }

    private static (string? Token, string? Id) ResolveTokenAndId(
        JobPosting posting, Regex urlPattern, string tokenGroup, string idGroup)
    {
        // Prefer fields the source populated; fall back to URL parsing for legacy postings.
        var token = posting.AtsToken;
        var id = posting.AtsId;
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(id))
        {
            return (token, id);
        }

        if (!string.IsNullOrEmpty(posting.Url))
        {
            var m = urlPattern.Match(posting.Url);
            if (m.Success)
            {
                token ??= m.Groups[tokenGroup].Value;
                id ??= m.Groups[idGroup].Value;
            }
        }

        return (token, id);
    }

    private static bool LooksLikeLoginWall(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        // Conservative: only flag URLs whose path strongly suggests an auth page.
        var lower = url.ToLowerInvariant();
        return lower.Contains("/login")
            || lower.Contains("/signin")
            || lower.Contains("/sign-in")
            || lower.Contains("/auth/")
            || lower.Contains("?login=")
            || lower.Contains("login_required");
    }
}
