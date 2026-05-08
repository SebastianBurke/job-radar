using System.Text.RegularExpressions;
using JobRadar.Core.Config;

namespace JobRadar.Scoring;

/// <summary>
/// Scans a JD for stack-tier hits and computes a numeric modifier the scorer
/// applies to the model's returned match score. The modifier rule:
///
///   +2 if a "primary" (.NET-stack) keyword appears AND no "mismatched" hit
///   -2 if a "mismatched" (Java/Python/Node…) keyword appears AND no primary
///    0 otherwise (both stacks mentioned, or neither)
///
/// Polyglot postings that name both .NET and Java/Python/etc. land at 0 — the
/// model is left to read the JD and decide whether .NET is the primary stack
/// or one of several. "Adjacent" hits (frontend frameworks, complementary
/// tech) are recorded but don't shift the modifier — they're surfaced to the
/// prompt for context only.
/// </summary>
public static class StackSignalsScanner
{
    public sealed record Result(
        int Modifier,
        IReadOnlyList<string> PrimaryHits,
        IReadOnlyList<string> AdjacentHits,
        IReadOnlyList<string> MismatchedHits)
    {
        public string PromptSummary => string.Concat(
            "primary: [", string.Join(", ", PrimaryHits), "]",
            " | adjacent: [", string.Join(", ", AdjacentHits), "]",
            " | mismatched: [", string.Join(", ", MismatchedHits), "]");
    }

    public static Result Scan(string text, StackSignalsConfig signals)
    {
        if (signals is null)
        {
            return new Result(0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
        text ??= string.Empty;

        var primary = MatchTerms(text, signals.Primary);
        var adjacent = MatchTerms(text, signals.Adjacent);
        var mismatched = MatchTerms(text, signals.Mismatched);

        var primaryHit = primary.Count > 0;
        var mismatchedHit = mismatched.Count > 0;

        // XOR: only shift when the JD points cleanly at one tier. Polyglot postings
        // (both primary and mismatched) get 0 — let the model judge from context.
        var modifier = (primaryHit, mismatchedHit) switch
        {
            (true, false) => +2,
            (false, true) => -2,
            _ => 0,
        };

        return new Result(modifier, primary, adjacent, mismatched);
    }

    /// <summary>Apply <paramref name="modifier"/> to <paramref name="score"/>, clamping to [1, 10].</summary>
    public static int ApplyModifier(int score, int modifier)
    {
        var adjusted = score + modifier;
        if (adjusted < 1) return 1;
        if (adjusted > 10) return 10;
        return adjusted;
    }

    private static List<string> MatchTerms(string text, List<string>? terms)
    {
        var hits = new List<string>();
        if (terms is null) return hits;
        foreach (var t in terms)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            // Use non-letter lookarounds (same convention as PostingFilters) so that
            // tokens with non-word characters like "C#" and ".NET" still match cleanly.
            var pat = $"(?<![A-Za-z]){Regex.Escape(t.Trim())}(?![A-Za-z])";
            if (Regex.IsMatch(text, pat, RegexOptions.IgnoreCase))
            {
                hits.Add(t.Trim());
            }
        }
        return hits;
    }
}
