namespace JobRadar.Core.Models;

public sealed record DigestEntry(JobPosting Posting, ScoringResult Score, DateTimeOffset FirstSeenAt)
{
    public ScoreBand Band => Score.MatchScore switch
    {
        >= 8 => ScoreBand.Top,
        >= 5 => ScoreBand.WorthALook,
        _ => ScoreBand.SanityCheck,
    };

    public bool IsCarryOver(DateTimeOffset now) => (now.Date - FirstSeenAt.UtcDateTime.Date).TotalDays >= 1;
}

public enum ScoreBand
{
    Top,
    WorthALook,
    SanityCheck,
}
