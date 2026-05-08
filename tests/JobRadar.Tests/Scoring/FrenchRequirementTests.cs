using System.Net;
using JobRadar.Core.Models;
using JobRadar.Scoring;
using JobRadar.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace JobRadar.Tests.Scoring;

/// <summary>
/// Documents the contract between the candidate's language profile (in
/// data/eligibility.md) and the scoring rubric in prompts/scoring-prompt.md.
/// Each scenario pairs a representative JD snippet with the verdict the model
/// is expected to return; the test wires up the scorer with a captured
/// Anthropic response and asserts the eligibility verdict propagates correctly.
///
/// These tests do not call the live LLM — they pin the parsing/contract
/// boundary so a regression in either the prompt or the scorer's JSON parsing
/// is caught in CI.
/// </summary>
public sealed class FrenchRequirementTests
{
    private static readonly string PromptPath = WritePromptFile();
    private static readonly string CvPath = WriteFile("CV: full stack .NET dev based in Montréal. French intermediate.");
    private static readonly string EligibilityPath = WriteFile(
        "languages:\n  fluent: [English, Spanish]\n  intermediate: [French]\n  none: []\n");

    private static string WritePromptFile()
    {
        var p = Path.GetTempFileName();
        File.WriteAllText(p,
            "## System\nbe terse\n## User\n{{cv}}\n{{eligibility}}\n{{posting.title}}\n{{posting.description}}");
        return p;
    }

    private static string WriteFile(string content)
    {
        var p = Path.GetTempFileName();
        File.WriteAllText(p, content);
        return p;
    }

    private static ClaudeScorer ScorerWithFixture(string fixtureRelativePath)
    {
        var body = File.ReadAllText(Path.Combine("Fixtures", "FrenchRequirement", fixtureRelativePath));
        var handler = new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
        return new ClaudeScorer(
            new StaticHttpClientFactory(handler),
            new ClaudeScorerOptions
            {
                ApiKey = "test-key",
                PromptPath = PromptPath,
                CvPath = CvPath,
                EligibilityPath = EligibilityPath,
                MaxRetries = 0,
            },
            NullLogger<ClaudeScorer>.Instance);
    }

    private static JobPosting JdPosting(string description, string title = "Senior .NET Developer") =>
        new(
            Source: "test",
            Company: "Acme",
            Title: title,
            Location: "Montréal, QC",
            Url: "https://x/jobs/1",
            Description: description);

    [Fact]
    public async Task Maitrise_du_francais_with_intermediate_french_candidate_is_ineligible()
    {
        var posting = JdPosting(
            "We are looking for a senior .NET developer. Maîtrise du français requise. Strong C# and Azure DevOps experience required.");
        var scorer = ScorerWithFixture("maitrise-ineligible.json");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(EligibilityVerdict.Ineligible, result.Eligibility);
        Assert.Contains("French", result.EligibilityReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task French_as_an_asset_with_intermediate_french_candidate_is_eligible()
    {
        var posting = JdPosting(
            "Looking for a full stack .NET developer. French is an asset but not required. C# / Angular / Azure.");
        var scorer = ScorerWithFixture("asset-eligible.json");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(EligibilityVerdict.Eligible, result.Eligibility);
    }

    [Fact]
    public async Task No_french_mentioned_with_intermediate_french_candidate_is_eligible()
    {
        var posting = JdPosting(
            "Senior .NET Engineer for our Madrid office (remote-friendly). Stack: C#, .NET 8, Azure DevOps. English-only working language.");
        var scorer = ScorerWithFixture("no-french-eligible.json");

        var result = await scorer.ScoreAsync(posting);

        Assert.Equal(EligibilityVerdict.Eligible, result.Eligibility);
    }

    [Fact]
    public void Prompt_includes_French_requirement_rubric_so_the_model_can_apply_it()
    {
        var promptText = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "prompts", "scoring-prompt.md"));

        // The rubric must call out fluent-French detection so the LLM can identify
        // disqualifying phrases. If this assertion fails, the prompt has drifted
        // and the verdicts above can no longer be trusted to match real runs.
        Assert.Contains("Maîtrise du français", promptText);
        Assert.Contains("French is an asset", promptText);
        Assert.Contains("languages", promptText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligibility_data_file_includes_structured_languages_block()
    {
        var path = Path.Combine("..", "..", "..", "..", "..", "data", "eligibility.md");
        var text = File.ReadAllText(path);

        Assert.Contains("languages:", text);
        Assert.Contains("fluent: [English, Spanish]", text);
        Assert.Contains("intermediate: [French]", text);
    }
}
