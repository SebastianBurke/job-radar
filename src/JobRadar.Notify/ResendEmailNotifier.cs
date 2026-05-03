using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using Microsoft.Extensions.Logging;

namespace JobRadar.Notify;

public sealed class ResendOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public bool DryRun { get; init; }
}

public sealed class ResendEmailNotifier : INotifier
{
    private const string ApiUrl = "https://api.resend.com/emails";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResendOptions _options;
    private readonly ILogger<ResendEmailNotifier> _logger;

    public ResendEmailNotifier(IHttpClientFactory httpClientFactory, ResendOptions options, ILogger<ResendEmailNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task SendDigestAsync(IReadOnlyList<DigestEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
        {
            _logger.LogInformation("No entries to send; skipping email.");
            return;
        }

        var subject = DigestRenderer.BuildSubject(entries);
        var html = DigestRenderer.BuildHtml(entries, DateTimeOffset.UtcNow);

        if (_options.DryRun)
        {
            _logger.LogInformation("DRY RUN — subject: {Subject}", subject);
            Console.WriteLine();
            Console.WriteLine($"=== Subject: {subject} ===");
            Console.WriteLine(html);
            Console.WriteLine($"=== End of digest ({entries.Count} entries) ===");
            Console.WriteLine();
            return;
        }

        if (string.IsNullOrEmpty(_options.ApiKey) || string.IsNullOrEmpty(_options.From) || string.IsNullOrEmpty(_options.To))
        {
            _logger.LogError("Resend not configured (RESEND_API_KEY/EMAIL_FROM/EMAIL_TO); cannot send email. Use --dry-run to preview.");
            return;
        }

        var http = _httpClientFactory.CreateClient("resend");
        var payload = new
        {
            from = _options.From,
            to = new[] { _options.To },
            subject,
            html,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Headers.UserAgent.ParseAdd("JobRadar/1.0 (personal-bot)");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Resend returned {Status}: {Body}", (int)resp.StatusCode, body);
            return;
        }

        _logger.LogInformation("Digest sent to {To}; {Count} entries.", _options.To, entries.Count);
    }
}
