using JobRadar.Core.Models;
using JobRadar.Notify;

namespace JobRadar.Tests.Notify;

public sealed class DigestRendererTests
{
    private static readonly DateTimeOffset Today = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    private static DigestEntry MakeEntry(
        int score,
        string title,
        EligibilityVerdict elig = EligibilityVerdict.Eligible,
        DateTimeOffset? firstSeenAt = null)
    {
        var posting = new JobPosting("greenhouse", "Acme Co", title, "Remote — Spain", "https://x/y?z=<script>", $"{title} description.");
        var result = new ScoringResult(score, elig, "ok",
            new[] { ".NET", "Angular", "Azure" }, "long ramp-up", "mid", "english", "€60k", "remote", "Solid fit.");
        return new DigestEntry(posting, result, firstSeenAt ?? Today);
    }

    [Fact]
    public void Subject_includes_count_and_top_score_when_all_new()
    {
        var subj = DigestRenderer.BuildSubject(
            new[] { MakeEntry(9, "A"), MakeEntry(4, "B"), MakeEntry(7, "C") },
            Today);
        Assert.Equal("[job-radar] 3 new postings — 9 top match", subj);
    }

    [Fact]
    public void Subject_distinguishes_new_and_pending_counts()
    {
        var subj = DigestRenderer.BuildSubject(
            new[]
            {
                MakeEntry(9, "Fresh"),
                MakeEntry(7, "OldA", firstSeenAt: Today.AddDays(-3)),
                MakeEntry(6, "OldB", firstSeenAt: Today.AddDays(-1)),
            },
            Today);
        Assert.Equal("[job-radar] 3 postings (1 new, 2 pending) — 9 top match", subj);
    }

    [Fact]
    public void Html_groups_by_band_and_escapes_user_content()
    {
        var html = DigestRenderer.BuildHtml(
            new[] { MakeEntry(9, "Top <pick>"), MakeEntry(6, "Mid"), MakeEntry(2, "Low") },
            Today);

        Assert.Contains("Top matches", html);
        Assert.Contains("Worth a look", html);
        Assert.Contains("Sanity check", html);
        Assert.Contains("Top &lt;pick&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Ambiguous_entries_get_visual_flag()
    {
        var html = DigestRenderer.BuildHtml(new[] { MakeEntry(8, "Ambig", EligibilityVerdict.Ambiguous) }, Today);
        Assert.Contains("eligibility unclear", html);
    }

    [Fact]
    public void Carry_over_entry_renders_pending_since_badge()
    {
        var html = DigestRenderer.BuildHtml(
            new[] { MakeEntry(8, "Old", firstSeenAt: new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero)) },
            Today);
        Assert.Contains("pending since Apr 28", html);
    }

    [Fact]
    public void Fresh_entry_does_not_render_pending_badge()
    {
        var html = DigestRenderer.BuildHtml(new[] { MakeEntry(8, "Fresh", firstSeenAt: Today) }, Today);
        Assert.DoesNotContain("pending since", html);
    }
}
