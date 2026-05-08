using System.Net;
using System.Net.Http.Headers;
using JobRadar.Core.Models;
using JobRadar.Scoring;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Scoring;

public sealed class ClaudeScorerTests
{
    private const string SuccessBody = """
        {
          "content":[
            {"type":"text","text":"{\"match_score\":7,\"eligibility\":\"eligible\",\"eligibility_reason\":\"OK.\",\"top_3_matched_skills\":[\"C#\",\"Azure\",\"Angular\"],\"top_concern\":\"x\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"Strong fit.\"}"}
          ]
        }
        """;

    private static ClaudeScorer NewScorerWith(StaticHttpHandler handler, int maxRetries = 3, JobRadar.Core.Config.StackSignalsConfig? signals = null)
    {
        var tempPrompt = Path.GetTempFileName();
        File.WriteAllText(tempPrompt, "## System\nbe terse\n## User\n{{cv}} {{posting.title}} stack={{stack_modifier}} matches={{stack_matches}}");
        var tempCv = Path.GetTempFileName();
        File.WriteAllText(tempCv, "CV");
        return new ClaudeScorer(
            new StaticHttpClientFactory(handler),
            new ClaudeScorerOptions
            {
                ApiKey = "test-key",
                PromptPath = tempPrompt,
                CvPath = tempCv,
                MaxRetries = maxRetries,
                StackSignals = signals ?? new JobRadar.Core.Config.StackSignalsConfig(),
            },
            NullLogger<ClaudeScorer>.Instance);
    }

    private static JobPosting AnyPosting() =>
        new("test", "Acme", ".NET Eng", "Remote", "https://x", "We need .NET work.");

    [Fact]
    public async Task Retries_on_429_and_returns_parsed_result_on_eventual_success()
    {
        var calls = 0;
        var handler = new StaticHttpHandler(_ =>
        {
            calls++;
            if (calls <= 2)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                // Tiny Retry-After so the test doesn't sleep meaningfully.
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return r;
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessBody),
            };
            return ok;
        });

        var scorer = NewScorerWith(handler);
        var result = await scorer.ScoreAsync(AnyPosting());

        Assert.Equal(7, result.MatchScore);
        Assert.Equal(EligibilityVerdict.Eligible, result.Eligibility);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Returns_fallback_with_status_code_after_exhausting_retries()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var scorer = NewScorerWith(handler, maxRetries: 1);

        var result = await scorer.ScoreAsync(AnyPosting());

        Assert.Equal(1, result.MatchScore);
        Assert.Equal(EligibilityVerdict.Ambiguous, result.Eligibility);
        Assert.Contains("503", result.EligibilityReason ?? "");
        Assert.Equal(2, handler.Requests.Count); // initial + 1 retry
    }

    [Fact]
    public async Task Does_not_retry_on_400_class_errors()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var scorer = NewScorerWith(handler);

        var result = await scorer.ScoreAsync(AnyPosting());

        Assert.Equal(1, result.MatchScore);
        Assert.Contains("400", result.EligibilityReason ?? "");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ParseResult_extracts_strict_json_block()
    {
        const string body = """
        {
          "id":"msg_x",
          "type":"message",
          "role":"assistant",
          "model":"claude-haiku-4-5-20251001",
          "content":[
            {"type":"text","text":"{\"match_score\":8,\"eligibility\":\"eligible\",\"eligibility_reason\":\"EU remote OK.\",\"top_3_matched_skills\":[\"C#\",\"Azure DevOps\",\"Angular\"],\"top_concern\":\"Senior backend years.\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":\"€55k-€70k\",\"remote_policy\":\"remote\",\"one_line_pitch\":\"Strong fit.\"}"}
          ],
          "stop_reason":"end_turn"
        }
        """;

        var result = ClaudeScorer.ParseResult(body);

        Assert.Equal(8, result.MatchScore);
        Assert.Equal(EligibilityVerdict.Eligible, result.Eligibility);
        Assert.Equal("remote", result.RemotePolicy);
        Assert.NotNull(result.Top3MatchedSkills);
        Assert.Equal(3, result.Top3MatchedSkills!.Count);
    }

    [Fact]
    public void ParseResult_strips_markdown_fence_and_returns_fallback_on_garbage()
    {
        const string fenced = """
        {
          "content":[
            {"type":"text","text":"```json\n{\"match_score\":3,\"eligibility\":\"ambiguous\",\"eligibility_reason\":\"unclear\",\"top_3_matched_skills\":[],\"top_concern\":\"x\",\"estimated_seniority\":\"junior\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"meh\"}\n```"}
          ]
        }
        """;
        var fenceResult = ClaudeScorer.ParseResult(fenced);
        Assert.Equal(3, fenceResult.MatchScore);
        Assert.Equal(EligibilityVerdict.Ambiguous, fenceResult.Eligibility);

        var garbage = ClaudeScorer.ParseResult("not json at all");
        Assert.Equal(EligibilityVerdict.Ambiguous, garbage.Eligibility);
        Assert.Equal(1, garbage.MatchScore);
    }

    [Fact]
    public void RenderUserMessage_replaces_all_placeholders()
    {
        const string template = "CV={{cv}} | E={{eligibility}} | T={{posting.title}} | C={{posting.company}} | L={{posting.location}} | S={{posting.source}} | U={{posting.url}} | D={{posting.description}}";
        var posting = new JobPosting("greenhouse", "Acme", ".NET Eng", "Remote", "https://example.com/1", "Lots of .NET");
        var rendered = ClaudeScorer.RenderUserMessage(template, posting, "MY-CV", "AUTH-CA-EU");

        Assert.Contains("CV=MY-CV", rendered);
        Assert.Contains("E=AUTH-CA-EU", rendered);
        Assert.Contains("T=.NET Eng", rendered);
        Assert.Contains("C=Acme", rendered);
        Assert.Contains("D=Lots of .NET", rendered);
    }

    [Fact]
    public void RenderUserMessage_renders_LocationConfidence_label_per_value()
    {
        const string template = "LC={{posting.location_confidence}}";
        var ats = new JobPosting(
            "greenhouse", "Acme", ".NET Eng", "Madrid", "https://x", "x",
            LocationConfidence: LocationConfidence.Authoritative);
        var aggregator = new JobPosting(
            "remoteok", "Acme", ".NET Eng", "Remote", "https://x", "x",
            LocationConfidence: LocationConfidence.AggregatorOnly);

        Assert.Contains("authoritative", ClaudeScorer.RenderUserMessage(template, ats, "", ""));
        Assert.Contains("aggregator-tag-only", ClaudeScorer.RenderUserMessage(template, aggregator, "", ""));
    }

    [Fact]
    public void RenderUserMessage_substitutes_stack_modifier_and_matches()
    {
        const string template = "MOD={{stack_modifier}} MATCHES={{stack_matches}}";
        var posting = new JobPosting("greenhouse", "Acme", ".NET Eng", "Remote", "https://x", "stuff");
        var scan = StackSignalsScanner.Scan("C# and .NET",
            new JobRadar.Core.Config.StackSignalsConfig
            {
                Primary = { ".NET", "C#" },
                Mismatched = { "Java" },
            });

        var rendered = ClaudeScorer.RenderUserMessage(template, posting, "", "", scan);

        Assert.Contains("MOD=+2", rendered);
        Assert.Contains("primary: [.NET, C#]", rendered);
    }

    [Fact]
    public async Task ScoreAsync_applies_positive_stack_modifier_and_clamps_at_10()
    {
        // Anthropic returns score=9; primary-only JD should bump to 10 (capped).
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"content":[{"type":"text","text":"{\"match_score\":9,\"eligibility\":\"eligible\",\"eligibility_reason\":\"ok\",\"top_3_matched_skills\":[\"C#\"],\"top_concern\":\"x\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"y\"}"}]}
                """),
        });
        var scorer = NewScorerWith(handler, signals: new JobRadar.Core.Config.StackSignalsConfig
        {
            Primary = { ".NET", "C#" },
            Mismatched = { "Java" },
        });
        var posting = new JobPosting("greenhouse", "Acme", "Senior .NET", "Remote", "https://x", "We need C# and .NET 8.");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(10, result.MatchScore);
    }

    [Fact]
    public async Task ScoreAsync_applies_negative_stack_modifier_for_mismatched_only()
    {
        // Anthropic returns score=7; mismatched-only JD downgrades to 5.
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"content":[{"type":"text","text":"{\"match_score\":7,\"eligibility\":\"eligible\",\"eligibility_reason\":\"ok\",\"top_3_matched_skills\":[\"backend\"],\"top_concern\":\"non-.NET stack\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"y\"}"}]}
                """),
        });
        var scorer = NewScorerWith(handler, signals: new JobRadar.Core.Config.StackSignalsConfig
        {
            Primary = { ".NET", "C#" },
            Mismatched = { "Java", "Spring" },
        });
        var posting = new JobPosting("workable", "Acme", "Backend dev", "Remote", "https://x", "Java, Spring Boot, JPA.");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(5, result.MatchScore);
    }

    [Fact]
    public async Task ScoreAsync_does_not_modify_score_when_neither_primary_nor_mismatched_hit()
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"content":[{"type":"text","text":"{\"match_score\":6,\"eligibility\":\"eligible\",\"eligibility_reason\":\"ok\",\"top_3_matched_skills\":[\"x\"],\"top_concern\":\"x\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"y\"}"}]}
                """),
        });
        var scorer = NewScorerWith(handler, signals: new JobRadar.Core.Config.StackSignalsConfig
        {
            Primary = { ".NET" },
            Mismatched = { "Java" },
        });
        var posting = new JobPosting("hackernews", "Acme", "Senior", "Remote", "https://x", "Generic JD with no stack keywords.");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(6, result.MatchScore);
    }
}
