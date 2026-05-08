namespace JobRadar.Core.Config;

public sealed class FiltersConfig
{
    public List<string> KeywordsCore { get; set; } = new();
    public List<string> KeywordsBroad { get; set; } = new();
    public List<string> TechContextHints { get; set; } = new();

    public List<string> LocationAllow { get; set; } = new();
    public List<string> LocationDenyPhrases { get; set; } = new();
    public int MaxScoringCallsPerRun { get; set; } = 200;
    public int PendingGraceDays { get; set; } = 30;

    public StackSignalsConfig StackSignals { get; set; } = new();
}

public sealed class StackSignalsConfig
{
    public List<string> Primary { get; set; } = new();
    public List<string> Adjacent { get; set; } = new();
    public List<string> Mismatched { get; set; } = new();
}
