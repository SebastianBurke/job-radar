using JobRadar.App;
using JobRadar.App.Config;
using JobRadar.Core.Models;

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

    [Fact]
    public void Repo_sources_yml_assigns_RequireOk_to_ATS_sources_and_BestEffort_to_aggregators()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var config = ConfigLoader.LoadSources(repoRoot);

        Assert.Equal(LiveCheckMode.RequireOk, config.LiveCheckModeFor("greenhouse"));
        Assert.Equal(LiveCheckMode.RequireOk, config.LiveCheckModeFor("lever"));
        Assert.Equal(LiveCheckMode.RequireOk, config.LiveCheckModeFor("ashby"));
        Assert.Equal(LiveCheckMode.RequireOk, config.LiveCheckModeFor("workable"));
        Assert.Equal(LiveCheckMode.BestEffort, config.LiveCheckModeFor("remoteok"));
        Assert.Equal(LiveCheckMode.BestEffort, config.LiveCheckModeFor("remotive"));
        Assert.Equal(LiveCheckMode.BestEffort, config.LiveCheckModeFor("weworkremotely"));
        Assert.Equal(LiveCheckMode.BestEffort, config.LiveCheckModeFor("hackernews"));
    }

    [Fact]
    public void LiveCheckModeFor_unknown_source_falls_back_to_None()
    {
        var config = new JobRadar.Core.Config.SourcesConfig();
        Assert.Equal(LiveCheckMode.None, config.LiveCheckModeFor("custom-source"));
    }

    [Fact]
    public void Repo_filters_yml_loads_stack_signals_block()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var config = ConfigLoader.LoadFilters(repoRoot);

        Assert.NotEmpty(config.StackSignals.Primary);
        Assert.NotEmpty(config.StackSignals.Mismatched);
        Assert.Contains(".NET", config.StackSignals.Primary);
        Assert.Contains("C#", config.StackSignals.Primary);
        Assert.Contains("Java", config.StackSignals.Mismatched);
    }
}
