using JobRadar.App.Filters;
using JobRadar.Core.Config;
using JobRadar.Core.Models;

namespace JobRadar.Tests.Filters;

public sealed class PostingFiltersTests
{
    private static FiltersConfig DefaultConfig() => new()
    {
        KeywordsRequired = new()
        {
            ".net", "c#", "dotnet", "asp.net", "blazor", "full stack", "fullstack",
            "software engineer", "software developer", "backend developer",
        },
        LocationAllow = new() { "montreal", "ottawa", "spain", "madrid", "barcelona", "remote", "europe", "canada" },
        LocationDenyPhrases = new() { "us only", "united states only", "must reside in the us", "h1b" },
        MaxScoringCallsPerRun = 200,
    };

    [Theory]
    [InlineData(".NET Developer", true)]
    [InlineData("Senior C# Engineer", true)]
    [InlineData("Full Stack Engineer", true)]
    [InlineData("Full-Stack Engineer", true)]
    [InlineData("Marketing Manager", false)]
    [InlineData("DevOps", false)]
    public void PassesKeyword_matches_expected_titles(string title, bool expected)
    {
        var filters = new PostingFilters(DefaultConfig());
        var posting = new JobPosting("test", "Acme", title, "Remote", "https://x", "(no description)");
        Assert.Equal(expected, filters.PassesKeyword(posting));
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
        var posting = new JobPosting("test", "Acme", "Engineer", location, "https://x", description);
        Assert.Equal(expected, filters.PassesLocation(posting));
    }

    [Fact]
    public void Keyword_does_not_match_partial_word()
    {
        var filters = new PostingFilters(DefaultConfig());
        var posting = new JobPosting("test", "Acme", "Magnetic Resonance", "Remote", "https://x", "no relevant keywords");
        Assert.False(filters.PassesKeyword(posting));
    }
}
