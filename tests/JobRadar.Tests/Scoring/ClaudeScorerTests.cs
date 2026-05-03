using JobRadar.Core.Models;
using JobRadar.Scoring;

namespace JobRadar.Tests.Scoring;

public sealed class ClaudeScorerTests
{
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
        const string template = "CV={{cv}} | T={{posting.title}} | C={{posting.company}} | L={{posting.location}} | S={{posting.source}} | U={{posting.url}} | D={{posting.description}}";
        var posting = new JobPosting("greenhouse", "Acme", ".NET Eng", "Remote", "https://example.com/1", "Lots of .NET");
        var rendered = ClaudeScorer.RenderUserMessage(template, posting, "MY-CV");

        Assert.Contains("CV=MY-CV", rendered);
        Assert.Contains("T=.NET Eng", rendered);
        Assert.Contains("C=Acme", rendered);
        Assert.Contains("D=Lots of .NET", rendered);
    }
}
