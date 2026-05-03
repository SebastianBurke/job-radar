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
    public int MaxTokens { get; init; } = 600;
    public int Concurrency { get; init; } = 5;
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

        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.UserAgent.ParseAdd("JobRadar/1.0 (personal-bot)");

        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic returned {Status} for {Company}/{Title}.", (int)resp.StatusCode, posting.Company, posting.Title);
                return ScoringResult.LowConfidenceFallback($"Anthropic HTTP {(int)resp.StatusCode}.");
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return ParseResult(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic call failed for {Company}/{Title}.", posting.Company, posting.Title);
            return ScoringResult.LowConfidenceFallback("Anthropic request error.");
        }
    }

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
