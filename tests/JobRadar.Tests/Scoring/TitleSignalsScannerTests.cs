using JobRadar.Core.Config;
using JobRadar.Scoring;

namespace JobRadar.Tests.Scoring;

public sealed class TitleSignalsScannerTests
{
    private static TitleSignalsConfig Config() => new()
    {
        SeniorTitleTerms = { "senior", "staff", "principal", "lead", "architect" },
        // Mirrors config/filters.yml — 3+ and 4+ in the threshold list so the
        // senior_mismatch rule catches the M3usa "Senior + 3+ years" case.
        SeniorYearsThresholds =
        {
            "3+ years", "minimum 3 years", "at least 3 years",
            "4+ years", "minimum 4 years", "at least 4 years",
            "5+ years", "minimum 5 years", "at least 5 years",
            "6+ years", "8+ years",
        },
        SeniorMismatchModifier = -2,
        SearchPlatformTerms = { "search engineer", "search platform", "search operations", "search specialist", "site search" },
        SearchPlatformBoost = 1,
        AccessibilityCanadaCaTerms = { "wcag", "accessibility", "gcweb", "wet-boew", "canada.ca" },
        AccessibilityCanadaCaBoost = 1,
    };

    [Fact]
    public void Senior_title_with_years_threshold_yields_minus_two()
    {
        var r = TitleSignalsScanner.Scan(
            title: "Senior .NET Engineer",
            description: "We need someone with 5+ years of production .NET experience.",
            Config());

        Assert.Equal(-2, r.Modifier);
        Assert.Contains("senior", r.SeniorMismatchHits);
        Assert.Contains("5+ years", r.SeniorMismatchHits);
    }

    [Theory]
    [InlineData("3+ years")]
    [InlineData("4+ years")]
    [InlineData("minimum 3 years")]
    [InlineData("at least 4 years")]
    public void Senior_title_with_lower_years_threshold_also_yields_minus_two(string yearsPhrase)
    {
        // The 5+ floor pre-fix missed JDs asking for 3 or 4 years of production
        // .NET, even though the gap is just as structural for a 0-years candidate.
        var r = TitleSignalsScanner.Scan(
            title: "Senior .NET Engineer",
            description: $"We need someone with {yearsPhrase} of production .NET experience.",
            Config());

        Assert.Equal(-2, r.Modifier);
        Assert.Contains("senior", r.SeniorMismatchHits);
        Assert.Contains(yearsPhrase, r.SeniorMismatchHits);
    }

    [Fact]
    public void Senior_title_alone_without_years_threshold_does_not_downgrade()
    {
        // "Senior" in title, no explicit years requirement → no senior_mismatch hit.
        var r = TitleSignalsScanner.Scan(
            title: "Senior .NET Engineer",
            description: "Owns the core platform. .NET, Azure, Postgres.",
            Config());

        Assert.Equal(0, r.Modifier);
        Assert.Empty(r.SeniorMismatchHits);
    }

    [Fact]
    public void Years_threshold_alone_without_senior_title_does_not_downgrade()
    {
        // "5+ years" in body, no senior in title → no senior_mismatch hit.
        var r = TitleSignalsScanner.Scan(
            title: "Software Engineer II",
            description: "5+ years of backend experience desired.",
            Config());

        Assert.Equal(0, r.Modifier);
        Assert.Empty(r.SeniorMismatchHits);
    }

    [Fact]
    public void Search_platform_title_yields_plus_one()
    {
        var r = TitleSignalsScanner.Scan(
            title: "Search Operations Engineer",
            description: "Run our search platform.",
            Config());

        Assert.Equal(1, r.Modifier);
        Assert.Contains("search operations", r.SearchPlatformHits);
    }

    [Fact]
    public void Accessibility_keywords_in_title_or_body_yield_plus_one()
    {
        var titleMatch = TitleSignalsScanner.Scan(
            title: "WCAG Accessibility Front-End Developer",
            description: "Build accessible UIs.",
            Config());
        Assert.Equal(1, titleMatch.Modifier);

        var bodyMatch = TitleSignalsScanner.Scan(
            title: "Front-End Developer",
            description: "Government CMS work; canada.ca templates.",
            Config());
        Assert.Equal(1, bodyMatch.Modifier);
    }

    [Fact]
    public void Modifiers_combine_additively()
    {
        // Senior + 5+ years + search platform → -2 + +1 = -1
        var r = TitleSignalsScanner.Scan(
            title: "Senior Search Platform Engineer",
            description: "8+ years owning our search infrastructure.",
            Config());

        Assert.Equal(-1, r.Modifier);
        Assert.NotEmpty(r.SeniorMismatchHits);
        Assert.Contains("search platform", r.SearchPlatformHits);
    }

    [Fact]
    public void No_signals_yields_zero_modifier()
    {
        var r = TitleSignalsScanner.Scan(
            title: "Frontend Developer",
            description: "React, TypeScript, REST APIs. Mid-level role.",
            Config());

        Assert.Equal(0, r.Modifier);
    }

    [Fact]
    public void Empty_config_returns_zero_modifier()
    {
        var r = TitleSignalsScanner.Scan(
            title: "Senior .NET Engineer",
            description: "5+ years required",
            new TitleSignalsConfig());

        Assert.Equal(0, r.Modifier);
    }

    [Fact]
    public void PromptSummary_groups_hits_per_tier()
    {
        var r = TitleSignalsScanner.Scan(
            title: "Senior Search Operations Engineer",
            description: "8+ years; canada.ca and GCWeb experience.",
            Config());

        Assert.Contains("senior_mismatch:", r.PromptSummary);
        Assert.Contains("search_platform: [search operations]", r.PromptSummary);
        Assert.Contains("accessibility:", r.PromptSummary);
    }
}
