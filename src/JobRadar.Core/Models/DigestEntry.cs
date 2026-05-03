namespace JobRadar.Core.Models;

public sealed record DigestEntry(JobPosting Posting, ScoringResult Score)
{
    public ScoreBand Band => Score.MatchScore switch
    {
        >= 8 => ScoreBand.Top,
        >= 5 => ScoreBand.WorthALook,
        _ => ScoreBand.SanityCheck,
    };
}

public enum ScoreBand
{
    Top,
    WorthALook,
    SanityCheck,
}
