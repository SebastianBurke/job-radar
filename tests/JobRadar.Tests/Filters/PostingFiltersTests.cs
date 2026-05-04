using JobRadar.App.Filters;
using JobRadar.Core.Config;
using JobRadar.Core.Models;

namespace JobRadar.Tests.Filters;

public sealed class PostingFiltersTests
{
    private static FiltersConfig DefaultConfig() => new()
    {
        KeywordsCore = new()
        {
            ".net", "c#", "dotnet", "asp.net", "blazor", ".net developer", ".net engineer", ".net core",
        },
        KeywordsBroad = new()
        {
            "software engineer", "software developer", "backend developer", "back-end developer",
            "full stack", "fullstack", "full-stack",
        },
        TechContextHints = new()
        {
            "c#", ".net", "dotnet", "asp.net", "azure", "entity framework", "blazor",
            "microsoft", "csharp", ".net core",
        },
        LocationAllow = new() { "montreal", "ottawa", "spain", "madrid", "barcelona", "remote", "europe", "canada" },
        LocationDenyPhrases = new() { "us only", "united states only", "must reside in the us", "h1b" },
        MaxScoringCallsPerRun = 200,
    };

    private static JobPosting Posting(string title, string description, string location = "Remote") =>
        new("test", "Acme", title, location, "https://x", description);

    [Theory]
    [InlineData(".NET Developer", "(no body)", true)]
    [InlineData("Senior C# Engineer", "(no body)", true)]
    [InlineData("Marketing Manager", "Marketing role.", false)]
    [InlineData("DevOps", "Kubernetes platform team.", false)]
    public void Core_keyword_in_title_passes_alone(string title, string desc, bool expected)
    {
        var filters = new PostingFilters(DefaultConfig());
        Assert.Equal(expected, filters.PassesKeyword(Posting(title, desc)));
    }

    // The four scenarios from the plan.
    [Fact]
    public void Core_keyword_overrides_unrelated_body()
    {
        // "Senior .NET Developer" + Rails description → passes (core hit)
        var filters = new PostingFilters(DefaultConfig());
        var p = Posting("Senior .NET Developer", "We're a Ruby on Rails shop building a monolith. Postgres, Sidekiq, etc.");
        Assert.True(filters.PassesKeyword(p));
    }

    [Fact]
    public void Broad_keyword_alone_without_tech_context_fails()
    {
        // "Software Engineer" alone, no .NET context anywhere → fails
        var filters = new PostingFilters(DefaultConfig());
        var p = Posting("Software Engineer", "Help us build a Go-based payments system. Postgres, gRPC, Kubernetes.");
        Assert.False(filters.PassesKeyword(p));
    }

    [Fact]
    public void Broad_keyword_with_tech_context_passes()
    {
        // "Software Engineer" + ".NET / Azure stack" in description → passes
        var filters = new PostingFilters(DefaultConfig());
        var p = Posting("Software Engineer", "Modern Azure / .NET stack, Entity Framework Core, ASP.NET. Hybrid work.");
        Assert.True(filters.PassesKeyword(p));
    }

    [Fact]
    public void Broad_keyword_with_unrelated_body_fails()
    {
        // "Software Engineer" + Rails monolith → fails
        var filters = new PostingFilters(DefaultConfig());
        var p = Posting("Software Engineer", "Rails monolith with Sidekiq, Heroku, Postgres. Strong Ruby idioms expected.");
        Assert.False(filters.PassesKeyword(p));
    }

    [Theory]
    [InlineData("Madrid, Spain", "", true)]
    [InlineData("Anywhere — Remote", "", true)]
    [InlineData("New York, NY", "", false)]
    [InlineData("Remote", "Remote (US Only)", false)]
    [InlineData("Remote", "Open to candidates anywhere in Europe", true)]
    public void PassesLocation_handles_allow_and_deny(string location, string description, bool expected)
    {
        var filters = new PostingFilters(DefaultConfig());
        var posting = Posting("Engineer", description, location);
        Assert.Equal(expected, filters.PassesLocation(posting));
    }

    [Fact]
    public void Keyword_does_not_match_partial_word()
    {
        var filters = new PostingFilters(DefaultConfig());
        Assert.False(filters.PassesKeyword(Posting("Magnetic Resonance", "no relevant keywords")));
    }
}
