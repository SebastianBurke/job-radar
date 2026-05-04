using JobRadar.App;
using JobRadar.App.Config;

namespace JobRadar.Tests.Config;

public sealed class ConfigLoaderTests
{
    // The naming-convention bug we hit in CI was: YAML key `weworkremotely:`
    // didn't deserialize into `WeWorkRemotely` because UnderscoredNamingConvention
    // expects `we_work_remotely:`. Tests that build SourcesConfig in C# don't
    // catch it. Loading the real file does.
    [Fact]
    public void Repo_sources_yml_populates_both_top_level_sections()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var config = ConfigLoader.LoadSources(repoRoot);

        Assert.NotEmpty(config.Remotive.SearchTerms);
        Assert.NotEmpty(config.WeWorkRemotely.Feeds);
    }

    [Fact]
    public void Repo_companies_yml_loads_with_at_least_one_resolved_ats()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var config = ConfigLoader.LoadCompanies(repoRoot);

        Assert.NotEmpty(config.Companies);
        Assert.Contains(config.Companies, c =>
            !string.Equals(c.Ats, "unknown", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(c.Token) && c.Token != "?");
    }

    [Fact]
    public void Repo_filters_yml_populates_keyword_tiers()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var config = ConfigLoader.LoadFilters(repoRoot);

        Assert.NotEmpty(config.KeywordsCore);
        Assert.NotEmpty(config.KeywordsBroad);
        Assert.NotEmpty(config.TechContextHints);
        Assert.True(config.MaxScoringCallsPerRun > 0);
    }
}
