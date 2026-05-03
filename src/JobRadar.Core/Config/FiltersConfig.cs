namespace JobRadar.Core.Config;

public sealed class FiltersConfig
{
    public List<string> KeywordsRequired { get; set; } = new();
    public List<string> LocationAllow { get; set; } = new();
    public List<string> LocationDenyPhrases { get; set; } = new();
    public int MaxScoringCallsPerRun { get; set; } = 200;
}
