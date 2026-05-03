using System.Text.Json.Serialization;

namespace JobRadar.Core.Models;

public sealed record ScoringResult(
    [property: JsonPropertyName("match_score")] int MatchScore,
    [property: JsonPropertyName("eligibility")] EligibilityVerdict Eligibility,
    [property: JsonPropertyName("eligibility_reason")] string? EligibilityReason,
    [property: JsonPropertyName("top_3_matched_skills")] IReadOnlyList<string>? Top3MatchedSkills,
    [property: JsonPropertyName("top_concern")] string? TopConcern,
    [property: JsonPropertyName("estimated_seniority")] string? EstimatedSeniority,
    [property: JsonPropertyName("language_required")] string? LanguageRequired,
    [property: JsonPropertyName("salary_listed")] string? SalaryListed,
    [property: JsonPropertyName("remote_policy")] string? RemotePolicy,
    [property: JsonPropertyName("one_line_pitch")] string? OneLinePitch)
{
    public static ScoringResult LowConfidenceFallback(string reason) =>
        new(
            MatchScore: 1,
            Eligibility: EligibilityVerdict.Ambiguous,
            EligibilityReason: reason,
            Top3MatchedSkills: Array.Empty<string>(),
            TopConcern: "Scorer could not parse posting; manual review required.",
            EstimatedSeniority: null,
            LanguageRequired: null,
            SalaryListed: null,
            RemotePolicy: null,
            OneLinePitch: null);
}
