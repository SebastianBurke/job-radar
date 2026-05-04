using JobRadar.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JobRadar.App.Config;

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static T LoadYaml<T>(string path) where T : new()
    {
        var text = File.ReadAllText(path);
        return Deserializer.Deserialize<T>(text) ?? new T();
    }

    public static CompaniesConfig LoadCompanies(string repoRoot) =>
        LoadYaml<CompaniesConfig>(Path.Combine(repoRoot, "config", "companies.yml"));

    public static FiltersConfig LoadFilters(string repoRoot) =>
        LoadYaml<FiltersConfig>(Path.Combine(repoRoot, "config", "filters.yml"));

    public static SourcesConfig LoadSources(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "config", "sources.yml");
        return File.Exists(path) ? LoadYaml<SourcesConfig>(path) : new SourcesConfig();
    }
}
