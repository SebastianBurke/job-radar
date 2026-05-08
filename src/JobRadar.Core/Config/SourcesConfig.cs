using JobRadar.Core.Models;

namespace JobRadar.Core.Config;

public sealed class SourcesConfig
{
    public RemotiveSourceConfig Remotive { get; set; } = new();
    public WeWorkRemotelySourceConfig WeWorkRemotely { get; set; } = new();
    public JobillicoSourceConfig Jobillico { get; set; } = new();
    public CapturedSourceConfig Captured { get; set; } = new();

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

    private static LiveCheckMode DefaultModeFor(string sourceName)
    {
        var name = (sourceName ?? string.Empty).ToLowerInvariant();
        return name switch
        {
            "greenhouse" or "lever" or "ashby" or "workable" => LiveCheckMode.RequireOk,
            "remoteok" or "remotive" or "weworkremotely" or "hackernews" or "jobillico" => LiveCheckMode.BestEffort,
            "gcjobs" => LiveCheckMode.BestEffort,
            // Workday-tenant captures (workday-cisco, workday-trendmicro, ...) and any
            // other captured-source flavour land here; the AtsLiveChecker's aggregator
            // path GETs the URL and looks for 4xx / login-wall markers.
            _ when name.StartsWith("workday-", StringComparison.Ordinal) => LiveCheckMode.BestEffort,
            _ when name.StartsWith("captured-", StringComparison.Ordinal) => LiveCheckMode.BestEffort,
            _ => LiveCheckMode.None,
        };
    }
}

public sealed class RemotiveSourceConfig
{
    public List<string> SearchTerms { get; set; } = new();
}

public sealed class WeWorkRemotelySourceConfig
{
    public List<string> Feeds { get; set; } = new();
}

public sealed class JobillicoSourceConfig
{
    /// <summary>Search keywords; the source iterates the cross product of terms × locations.</summary>
    public List<string> SearchTerms { get; set; } = new();
    public List<string> Locations { get; set; } = new();
}

public sealed class CapturedSourceConfig
{
    /// <summary>
    /// Directory containing JSON files produced by an external capture process
    /// (typically a headless-browser sibling instance — see HEADLESS-SOURCE-HANDOFF.md).
    /// Path is relative to the repo root unless absolute. Each file in this dir
    /// matching <c>*.json</c> is read on every run.
    /// </summary>
    public string Directory { get; set; } = "data/captured";

    /// <summary>
    /// Captures older than this in days log a warning. Stale captures still get
    /// used — the warning just surfaces the freshness gap in run logs.
    /// </summary>
    public int StalenessWarningDays { get; set; } = 7;
}
