using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Models;
using Microsoft.Extensions.Logging;

namespace JobRadar.Scoring;

public sealed class ClaudeScorerOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 1500;
    public int Concurrency { get; init; } = 2;
    public int MaxRetries { get; init; } = 3;
    public string PromptPath { get; init; } = string.Empty;
    public string CvPath { get; init; } = string.Empty;
}

public sealed class ClaudeScorer : IScorer
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ClaudeScorerOptions _options;
    private readonly ILogger<ClaudeScorer> _logger;
    private readonly Lazy<(string SystemPrompt, string UserTemplate)> _prompt;
    private readonly Lazy<string> _cv;

    public ClaudeScorer(
        IHttpClientFactory httpClientFactory,
        ClaudeScorerOptions options,
        ILogger<ClaudeScorer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _prompt = new Lazy<(string, string)>(() => LoadPrompt(_options.PromptPath));
        _cv = new Lazy<string>(() => File.ReadAllText(_options.CvPath));
    }

    public async Task<ScoringResult> ScoreAsync(JobPosting posting, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            _logger.LogWarning("ANTHROPIC_API_KEY not set; returning fallback for {Company}/{Title}.", posting.Company, posting.Title);
            return ScoringResult.LowConfidenceFallback("ANTHROPIC_API_KEY not configured.");
        }

        var (systemPrompt, userTemplate) = _prompt.Value;
        var userMessage = RenderUserMessage(userTemplate, posting, _cv.Value);

        var request = new
        {
            model = _options.Model,
            max_tokens = _options.MaxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userMessage },
            },
        };

        var http = _httpClientFactory.CreateClient("anthropic");
        http.Timeout = TimeSpan.FromSeconds(60);

        var serialized = JsonSerializer.Serialize(request);
        var label = $"{posting.Company}/{posting.Title}";

        HttpResponseMessage resp;
        try
        {
            resp = await SendWithRetryAsync(http, () => BuildRequest(serialized), label, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anthropic call failed for {Label}.", label);
            return ScoringResult.LowConfidenceFallback("Anthropic request error.");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic returned {Status} after retries for {Label}.", (int)resp.StatusCode, label);
                return ScoringResult.LowConfidenceFallback($"Anthropic HTTP {(int)resp.StatusCode}.");
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseResult(body);
            if (IsFallback(parsed))
            {
                _logger.LogWarning("Anthropic parse fallback for {Label}: {Reason}", label, parsed.EligibilityReason);
            }
            return parsed;
        }
    }

    private HttpRequestMessage BuildRequest(string serializedJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(serializedJson, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.UserAgent.ParseAdd("JobRadar/1.0 (personal-bot)");
        return req;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        string label,
        CancellationToken ct)
    {
        HttpResponseMessage? lastResp = null;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var defaultDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // 1s, 2s, 4s, …
                var wait = ResolveRetryAfter(lastResp) ?? defaultDelay;
                _logger.LogInformation(
                    "Anthropic retry {Attempt}/{Max} for {Label} after {Wait:F1}s ({Reason}).",
                    attempt, _options.MaxRetries, label, wait.TotalSeconds,
                    lastResp is null ? "exception" : $"HTTP {(int)lastResp.StatusCode}");
                await Task.Delay(wait, ct);
            }

            lastResp?.Dispose();
            lastResp = null;
            HttpRequestMessage? req = null;
            try
            {
                req = requestFactory();
                lastResp = await http.SendAsync(req, ct);
                if (!IsRetryable(lastResp.StatusCode))
                {
                    return lastResp;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
            finally
            {
                req?.Dispose();
            }
        }

        if (lastResp is not null) return lastResp;
        throw lastException ?? new InvalidOperationException("Anthropic call failed after retries.");
    }

    private static bool IsRetryable(System.Net.HttpStatusCode code)
    {
        var n = (int)code;
        return n == 429 || n == 529 || (n >= 500 && n <= 599);
    }

    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage? resp)
    {
        if (resp?.Headers.RetryAfter is null) return null;
        var ra = resp.Headers.RetryAfter;
        if (ra.Delta is { } delta && delta > TimeSpan.Zero) return Cap(delta);
        if (ra.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return Cap(wait);
        }
        return null;
    }

    private static TimeSpan Cap(TimeSpan t) => t > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : t;

    public async IAsyncEnumerable<(JobPosting Posting, ScoringResult Score)> ScoreManyAsync(
        IEnumerable<JobPosting> postings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<(JobPosting, ScoringResult)>(new UnboundedChannelOptions { SingleReader = true });
        using var gate = new SemaphoreSlim(Math.Max(1, _options.Concurrency));

        var workers = new List<Task>();
        foreach (var posting in postings)
        {
            await gate.WaitAsync(ct);
            workers.Add(Task.Run(async () =>
            {
                try
                {
                    var score = await ScoreAsync(posting, ct);
                    await channel.Writer.WriteAsync((posting, score), ct);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        _ = Task.WhenAll(workers).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    private static (string System, string User) LoadPrompt(string path)
    {
        var text = File.ReadAllText(path);
        // The prompt file uses '## System' and '## User' headings separated by '---'.
        var systemIdx = text.IndexOf("## System", StringComparison.Ordinal);
        var userIdx = text.IndexOf("## User", StringComparison.Ordinal);
        if (systemIdx < 0 || userIdx < 0)
        {
            throw new InvalidOperationException("Prompt file must contain '## System' and '## User' sections.");
        }
        var systemBlock = text[(systemIdx + "## System".Length)..userIdx].Trim().Trim('-').Trim();
        var userBlock = text[(userIdx + "## User".Length)..].Trim();
        return (systemBlock, userBlock);
    }

    public static string RenderUserMessage(string template, JobPosting posting, string cv) =>
        template
            .Replace("{{cv}}", cv, StringComparison.Ordinal)
            .Replace("{{posting.title}}", posting.Title, StringComparison.Ordinal)
            .Replace("{{posting.company}}", posting.Company, StringComparison.Ordinal)
            .Replace("{{posting.location}}", posting.Location, StringComparison.Ordinal)
            .Replace("{{posting.source}}", posting.Source, StringComparison.Ordinal)
            .Replace("{{posting.url}}", posting.Url, StringComparison.Ordinal)
            .Replace("{{posting.description}}", posting.Description, StringComparison.Ordinal);

    public static ScoringResult ParseResult(string anthropicResponseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(anthropicResponseBody);
            // Anthropic Messages API responds with { content: [ { type: "text", text: "..." } ], ... }
            var content = doc.RootElement.GetProperty("content");
            string? rawText = null;
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var x))
                {
                    rawText = x.GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(rawText))
            {
                return ScoringResult.LowConfidenceFallback("Anthropic response had no text block.");
            }

            // Defensive: strip markdown fences if the model added them despite instructions.
            var json = ExtractJsonObject(rawText);
            var result = JsonSerializer.Deserialize<ScoringResult>(json, ParseOpts);
            return result ?? ScoringResult.LowConfidenceFallback("Scorer returned null.");
        }
        catch (JsonException ex)
        {
            return ScoringResult.LowConfidenceFallback($"JSON parse error: {ex.Message}");
        }
    }

    private static bool IsFallback(ScoringResult s) =>
        s.MatchScore == 1
        && s.Eligibility == EligibilityVerdict.Ambiguous
        && (s.Top3MatchedSkills is null || s.Top3MatchedSkills.Count == 0);

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return (start >= 0 && end > start) ? text[start..(end + 1)] : text;
    }

    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
