using System.Text.RegularExpressions;
using JobRadar.Core.Config;

namespace JobRadar.Scoring;

/// <summary>
/// Scans a JD's title and description for seniority / search-platform /
/// accessibility signals tied to the candidate's actual experience profile.
/// Returns a numeric modifier the scorer applies to the model's match_score.
///
/// Rules (additive; a single posting can trip multiple):
///   senior_title in title AND a years-threshold in title or body  → -2
///       The candidate has 0 years production .NET, so "Senior X, 5+ years"
///       is a structural mismatch, not a stretch.
///   search_platform_terms in title                                 → +1
///       The day-job is search-platform technical analysis at canada.ca;
///       "Search Operations Engineer" / "Site Search" titles are a direct
///       fit, not a stretch.
///   accessibility_canada_ca_terms in title or body                 → +1
///       Production overlap: WCAG, GCWeb, WET-BOEW, canada.ca.
///
/// "Junior / Mid / Software Engineer I or II / Intermediate" titles are
/// scored at face value (no boost, no penalty) — listed in the prompt
/// rubric only, not handled here.
/// </summary>
public static class TitleSignalsScanner
{
    public sealed record Result(
        int Modifier,
        IReadOnlyList<string> SeniorMismatchHits,
        IReadOnlyList<string> SearchPlatformHits,
        IReadOnlyList<string> AccessibilityHits)
    {
        public string PromptSummary => string.Concat(
            "senior_mismatch: [", string.Join(", ", SeniorMismatchHits), "]",
            " | search_platform: [", string.Join(", ", SearchPlatformHits), "]",
            " | accessibility: [", string.Join(", ", AccessibilityHits), "]");
    }

    public static Result Scan(string title, string description, TitleSignalsConfig config)
    {
        if (config is null)
        {
            return new Result(0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
        title ??= string.Empty;
        description ??= string.Empty;
        var combined = title + "\n" + description;

        // Senior mismatch needs both a senior title qualifier AND a years threshold.
        // The years threshold can land in either the title or the body — JDs vary.
        var seniorTitleHits = MatchAny(title, config.SeniorTitleTerms);
        var yearsHits = MatchAny(combined, config.SeniorYearsThresholds);
        var seniorMismatchHits = (seniorTitleHits.Count > 0 && yearsHits.Count > 0)
            ? seniorTitleHits.Concat(yearsHits).ToList()
            : new List<string>();

        // Search platform: title-only — body mentions of "search" are too noisy
        // (every JD mentions some kind of "search functionality").
        var searchHits = MatchAny(title, config.SearchPlatformTerms);

        // Accessibility / canada.ca: title or body. Production overlap signal.
        var a11yHits = MatchAny(combined, config.AccessibilityCanadaCaTerms);

        var modifier =
            (seniorMismatchHits.Count > 0 ? config.SeniorMismatchModifier : 0)
            + (searchHits.Count > 0 ? config.SearchPlatformBoost : 0)
            + (a11yHits.Count > 0 ? config.AccessibilityCanadaCaBoost : 0);

        return new Result(modifier, seniorMismatchHits, searchHits, a11yHits);
    }

    private static List<string> MatchAny(string haystack, List<string>? terms)
    {
        var hits = new List<string>();
        if (terms is null || string.IsNullOrEmpty(haystack)) return hits;
        foreach (var t in terms)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            // Non-letter lookarounds (same convention as PostingFilters and
            // StackSignalsScanner) so terms with non-word chars like "5+ years"
            // and "wet-boew" and "canada.ca" still match cleanly.
            var pat = $"(?<![A-Za-z]){Regex.Escape(t.Trim())}(?![A-Za-z])";
            if (Regex.IsMatch(haystack, pat, RegexOptions.IgnoreCase))
            {
                hits.Add(t.Trim());
            }
        }
        return hits;
    }
}
