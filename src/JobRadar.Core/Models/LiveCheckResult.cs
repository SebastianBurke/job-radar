namespace JobRadar.Core.Models;

public sealed record LiveCheckResult(LiveCheckVerdict Verdict, string Reason)
{
    public static LiveCheckResult Live(string reason = "ATS confirmed") => new(LiveCheckVerdict.Live, reason);
    public static LiveCheckResult Dead(string reason) => new(LiveCheckVerdict.Dead, reason);
    public static LiveCheckResult Unknown(string reason) => new(LiveCheckVerdict.Unknown, reason);
    public static LiveCheckResult Skipped() => new(LiveCheckVerdict.Live, "live-check disabled");
}
