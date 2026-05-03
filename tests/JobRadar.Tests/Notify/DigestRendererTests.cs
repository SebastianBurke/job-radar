using JobRadar.Core.Models;
using JobRadar.Notify;

namespace JobRadar.Tests.Notify;

public sealed class DigestRendererTests
{
    private static DigestEntry MakeEntry(int score, string title, EligibilityVerdict elig = EligibilityVerdict.Eligible)
    {
        var posting = new JobPosting("greenhouse", "Acme Co", title, "Remote — Spain", "https://x/y?z=<script>", $"{title} description.");
        var result = new ScoringResult(score, elig, "ok",
            new[] { ".NET", "Angular", "Azure" }, "long ramp-up", "mid", "english", "€60k", "remote", "Solid fit.");
        return new DigestEntry(posting, result);
    }

    [Fact]
    public void Subject_includes_count_and_top_score()
    {
        var subj = DigestRenderer.BuildSubject(new[] { MakeEntry(9, "A"), MakeEntry(4, "B"), MakeEntry(7, "C") });
        Assert.Equal("[job-radar] 3 new postings — 9 top match", subj);
    }

    [Fact]
    public void Html_groups_by_band_and_escapes_user_content()
    {
        var html = DigestRenderer.BuildHtml(
            new[] { MakeEntry(9, "Top <pick>"), MakeEntry(6, "Mid"), MakeEntry(2, "Low") },
            new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("Top matches", html);
        Assert.Contains("Worth a look", html);
        Assert.Contains("Sanity check", html);
        Assert.Contains("Top &lt;pick&gt;", html);  // escaped
        Assert.DoesNotContain("<script>", html);  // url is escaped too
    }

    [Fact]
    public void Ambiguous_entries_get_visual_flag()
    {
        var html = DigestRenderer.BuildHtml(new[] { MakeEntry(8, "Ambig", EligibilityVerdict.Ambiguous) }, DateTimeOffset.UtcNow);
        Assert.Contains("eligibility unclear", html);
    }
}
