using JobRadar.Core.Models;

namespace JobRadar.Core.Config;

public sealed class SourcesConfig
{
    public RemotiveSourceConfig Remotive { get; set; } = new();
    public WeWorkRemotelySourceConfig WeWorkRemotely { get; set; } = new();

    /// <summary>
    /// Per-source live-check mode keyed by <see cref="JobPosting.Source"/> (e.g. "greenhouse", "remoteok").
    /// Values: "require_ok", "best_effort", "none". Missing keys fall back to a sensible default
    /// per source family — ATS sources default to <see cref="LiveCheckMode.RequireOk"/>, aggregator
    /// sources default to <see cref="LiveCheckMode.BestEffort"/>.
    /// </summary>
    public Dictionary<string, string> LiveCheck { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public LiveCheckMode LiveCheckModeFor(string sourceName)
    {
        if (LiveCheck.TryGetValue(sourceName, out var raw)
            && TryParseMode(raw, out var explicitMode))
        {
            return explicitMode;
        }
        return DefaultModeFor(sourceName);
    }

    private static bool TryParseMode(string raw, out LiveCheckMode mode)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "require_ok":
            case "requireok":
                mode = LiveCheckMode.RequireOk;
                return true;
            case "best_effort":
            case "besteffort":
                mode = LiveCheckMode.BestEffort;
                return true;
            case "none":
            case "off":
            case "disabled":
                mode = LiveCheckMode.None;
                return true;
            default:
                mode = LiveCheckMode.None;
                return false;
        }
    }

    private static LiveCheckMode DefaultModeFor(string sourceName) => (sourceName ?? string.Empty).ToLowerInvariant() switch
    {
        "greenhouse" or "lever" or "ashby" or "workable" => LiveCheckMode.RequireOk,
        "remoteok" or "remotive" or "weworkremotely" or "hackernews" => LiveCheckMode.BestEffort,
        _ => LiveCheckMode.None,
    };
}

public sealed class RemotiveSourceConfig
{
    public List<string> SearchTerms { get; set; } = new();
}

public sealed class WeWorkRemotelySourceConfig
{
    public List<string> Feeds { get; set; } = new();
}
