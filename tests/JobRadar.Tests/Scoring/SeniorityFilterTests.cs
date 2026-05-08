using System.Net;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Scoring;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Scoring;

/// <summary>
/// End-to-end checks that the title-modifier rules from
/// <c>config/filters.yml</c> + <c>prompts/scoring-prompt.md</c> produce the
/// expected score deltas between adjacent JDs that differ only in title.
/// The Anthropic mock returns a fixed score for every call, so any score
/// difference between the two postings is attributable to the post-hoc
/// modifier alone.
/// </summary>
public sealed class SeniorityFilterTests
{
    private const int FixedModelScore = 7;

    private static readonly string FixedAnthropicBody = $$"""
        {"content":[{"type":"text","text":"{\"match_score\":{{FixedModelScore}},\"eligibility\":\"eligible\",\"eligibility_reason\":\"ok\",\"top_3_matched_skills\":[\"x\",\"y\",\"z\"],\"top_concern\":\"x\",\"estimated_seniority\":\"mid\",\"language_required\":\"english\",\"salary_listed\":null,\"remote_policy\":\"remote\",\"one_line_pitch\":\"y\"}"}]}
        """;

    private static TitleSignalsConfig DefaultTitleSignals() => new()
    {
        SeniorTitleTerms = { "senior", "staff", "principal", "lead", "architect" },
        SeniorYearsThresholds = { "5+ years", "minimum 5 years", "at least 5 years", "6+ years", "8+ years" },
        SeniorMismatchModifier = -2,
        SearchPlatformTerms = { "search engineer", "search platform", "search operations", "search specialist", "site search" },
        SearchPlatformBoost = 1,
        AccessibilityCanadaCaTerms = { "wcag", "accessibility", "gcweb", "wet-boew", "canada.ca" },
        AccessibilityCanadaCaBoost = 1,
    };

    private static ClaudeScorer NewScorer(TitleSignalsConfig titleSignals)
    {
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FixedAnthropicBody),
        });

        var promptPath = Path.GetTempFileName();
        File.WriteAllText(promptPath, "## System\nbe terse\n## User\n{{posting.title}} {{posting.description}}");
        var cvPath = Path.GetTempFileName();
        File.WriteAllText(cvPath, "CV");
        var eligibilityPath = Path.GetTempFileName();
        File.WriteAllText(eligibilityPath, "eligibility");

        return new ClaudeScorer(
            new StaticHttpClientFactory(handler),
            new ClaudeScorerOptions
            {
                ApiKey = "test-key",
                PromptPath = promptPath,
                CvPath = cvPath,
                EligibilityPath = eligibilityPath,
                MaxRetries = 0,
                // No stack signals — we want the title modifier to be the only thing
                // shifting the score between the two postings under test.
                StackSignals = new StackSignalsConfig(),
                TitleSignals = titleSignals,
            },
            NullLogger<ClaudeScorer>.Instance);
    }

    private static JobPosting Posting(string title, string description) =>
        new(
            Source: "test",
            Company: "Acme",
            Title: title,
            Location: "Remote",
            Url: "https://x/" + Guid.NewGuid().ToString("N"),
            Description: description);

    [Fact]
    public async Task Senior_dotnet_with_5_plus_years_scores_2_lower_than_software_engineer_ii()
    {
        var scorer = NewScorer(DefaultTitleSignals());
        const string body = "We need a backend engineer with 5+ years of production .NET experience to own our platform.";

        var senior = await scorer.ScoreAsync(Posting("Senior .NET Engineer", body));
        var swEng2 = await scorer.ScoreAsync(Posting("Software Engineer II", body));

        Assert.Equal(2, swEng2.MatchScore - senior.MatchScore);
        Assert.Equal(FixedModelScore - 2, senior.MatchScore);
        Assert.Equal(FixedModelScore, swEng2.MatchScore);
    }

    [Fact]
    public async Task Search_operations_engineer_scores_1_higher_than_software_engineer()
    {
        var scorer = NewScorer(DefaultTitleSignals());
        const string body = "Build and operate a high-traffic platform. Strong backend experience.";

        var search = await scorer.ScoreAsync(Posting("Search Operations Engineer", body));
        var swEng = await scorer.ScoreAsync(Posting("Software Engineer", body));

        Assert.Equal(1, search.MatchScore - swEng.MatchScore);
        Assert.Equal(FixedModelScore + 1, search.MatchScore);
        Assert.Equal(FixedModelScore, swEng.MatchScore);
    }

    [Fact]
    public async Task WCAG_accessibility_frontend_scores_1_higher_than_generic_frontend()
    {
        var scorer = NewScorer(DefaultTitleSignals());

        var a11y = await scorer.ScoreAsync(Posting(
            "WCAG / Accessibility Front-End Developer",
            "Build inclusive web apps with semantic HTML and ARIA."));
        var generic = await scorer.ScoreAsync(Posting(
            "Front-End Developer",
            "Build web apps in React and TypeScript."));

        Assert.Equal(1, a11y.MatchScore - generic.MatchScore);
        Assert.Equal(FixedModelScore + 1, a11y.MatchScore);
        Assert.Equal(FixedModelScore, generic.MatchScore);
    }

    [Fact]
    public async Task Senior_search_engineer_with_5_plus_years_nets_to_minus_one()
    {
        // Senior + years (-2) plus search platform (+1) → -1 net.
        var scorer = NewScorer(DefaultTitleSignals());

        var senior = await scorer.ScoreAsync(Posting(
            "Senior Search Platform Engineer",
            "8+ years owning a high-traffic search platform."));

        Assert.Equal(FixedModelScore - 1, senior.MatchScore);
    }
}
