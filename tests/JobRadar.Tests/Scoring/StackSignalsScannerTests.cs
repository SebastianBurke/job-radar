using JobRadar.Core.Config;
using JobRadar.Scoring;

namespace JobRadar.Tests.Scoring;

public sealed class StackSignalsScannerTests
{
    private static StackSignalsConfig Signals() => new()
    {
        Primary = { ".NET", "C#", "ASP.NET", "Blazor" },
        Adjacent = { "TypeScript", "React", "Angular" },
        Mismatched = { "Java", "Spring", "Python", "Django", "Node.js", "Kotlin", "Ruby on Rails", "Go", "PHP" },
    };

    [Fact]
    public void Pure_dotnet_jd_yields_modifier_plus_two()
    {
        var jd = "We're hiring a Senior C# / .NET developer to work on Blazor and ASP.NET Core APIs.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        Assert.Equal(2, r.Modifier);
        Assert.Contains(".NET", r.PrimaryHits);
        Assert.Contains("C#", r.PrimaryHits);
        Assert.Empty(r.MismatchedHits);
    }

    [Fact]
    public void Pure_java_jd_yields_modifier_minus_two()
    {
        var jd = "Senior Software Engineer — Java / Spring Boot / PostgreSQL.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        Assert.Equal(-2, r.Modifier);
        Assert.Contains("Java", r.MismatchedHits);
        Assert.Contains("Spring", r.MismatchedHits);
        Assert.Empty(r.PrimaryHits);
    }

    [Fact]
    public void Polyglot_jd_with_both_primary_and_mismatched_yields_modifier_zero()
    {
        var jd = "We use a polyglot stack: C# for the API, Java for the legacy system, Python for ML.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        Assert.Equal(0, r.Modifier);
        Assert.Contains("C#", r.PrimaryHits);
        Assert.Contains("Java", r.MismatchedHits);
    }

    [Fact]
    public void Jd_without_any_stack_keywords_yields_modifier_zero()
    {
        var jd = "We're looking for a senior data engineer with strong SQL and ETL experience.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        Assert.Equal(0, r.Modifier);
        Assert.Empty(r.PrimaryHits);
        Assert.Empty(r.MismatchedHits);
    }

    [Fact]
    public void Adjacent_only_does_not_shift_modifier()
    {
        var jd = "Frontend lead — TypeScript, React, GraphQL.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        // No primary, no mismatched → 0 even with adjacent hits.
        Assert.Equal(0, r.Modifier);
        Assert.Contains("TypeScript", r.AdjacentHits);
        Assert.Contains("React", r.AdjacentHits);
    }

    [Fact]
    public void ApplyModifier_clamps_to_1_and_10()
    {
        Assert.Equal(10, StackSignalsScanner.ApplyModifier(score: 9, modifier: 2));
        Assert.Equal(1, StackSignalsScanner.ApplyModifier(score: 2, modifier: -3));
        Assert.Equal(7, StackSignalsScanner.ApplyModifier(score: 5, modifier: 2));
        Assert.Equal(3, StackSignalsScanner.ApplyModifier(score: 5, modifier: -2));
    }

    [Fact]
    public void PromptSummary_groups_hits_by_tier()
    {
        var jd = "C# developer; React frontend; Python scripts somewhere.";
        var r = StackSignalsScanner.Scan(jd, Signals());

        Assert.Contains("primary: [C#]", r.PromptSummary);
        Assert.Contains("adjacent: [React]", r.PromptSummary);
        Assert.Contains("mismatched: [Python]", r.PromptSummary);
    }

    [Fact]
    public void Empty_signals_config_returns_zero_modifier_and_no_hits()
    {
        var r = StackSignalsScanner.Scan("anything goes", new StackSignalsConfig());

        Assert.Equal(0, r.Modifier);
        Assert.Empty(r.PrimaryHits);
        Assert.Empty(r.AdjacentHits);
        Assert.Empty(r.MismatchedHits);
    }
}
