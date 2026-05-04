namespace JobRadar.Core.Config;

public sealed class FiltersConfig
{
    public List<string> KeywordsCore { get; set; } = new();
    public List<string> KeywordsBroad { get; set; } = new();
    public List<string> TechContextHints { get; set; } = new();

    public List<string> LocationAllow { get; set; } = new();
    public List<string> LocationDenyPhrases { get; set; } = new();
    public int MaxScoringCallsPerRun { get; set; } = 200;
}
