namespace JobRadar.App;

public enum RunMode
{
    Scan,           // default: fetch + score + send digest
    MarkApplied,    // append URL to data/applied.yml under 'applied:' and exit
    Dismiss,        // append URL to data/applied.yml under 'dismissed:' and exit
    ListPending,    // print pending postings and exit
}

public sealed class RuntimeOptions
{
    public RunMode Mode { get; set; } = RunMode.Scan;
    public bool DryRun { get; set; }
    public string RepoRoot { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
    public string? Note { get; set; }
    public string? UsageError { get; set; }

    public static RuntimeOptions FromArgs(string[] args, string repoRoot)
    {
        var opts = new RuntimeOptions { RepoRoot = repoRoot };

        // Single-pass parser: extract --note first since it's an option for the mode flags,
        // then resolve the mode (dry-run vs mark-applied vs dismiss vs list-pending).
        var modes = new List<(RunMode mode, string? value)>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                opts.DryRun = true;
            }
            else if (string.Equals(a, "--mark-applied", StringComparison.OrdinalIgnoreCase))
            {
                modes.Add((RunMode.MarkApplied, i + 1 < args.Length ? args[++i] : null));
            }
            else if (string.Equals(a, "--dismiss", StringComparison.OrdinalIgnoreCase))
            {
                modes.Add((RunMode.Dismiss, i + 1 < args.Length ? args[++i] : null));
            }
            else if (string.Equals(a, "--list-pending", StringComparison.OrdinalIgnoreCase))
            {
                modes.Add((RunMode.ListPending, null));
            }
            else if (string.Equals(a, "--note", StringComparison.OrdinalIgnoreCase))
            {
                opts.Note = i + 1 < args.Length ? args[++i] : null;
            }
        }

        if (modes.Count > 1)
        {
            opts.UsageError = "Specify at most one of --mark-applied, --dismiss, --list-pending.";
            return opts;
        }

        if (modes.Count == 1)
        {
            var (mode, value) = modes[0];
            opts.Mode = mode;
            opts.TargetUrl = value;
            if ((mode == RunMode.MarkApplied || mode == RunMode.Dismiss) && string.IsNullOrWhiteSpace(value))
            {
                opts.UsageError = $"{(mode == RunMode.MarkApplied ? "--mark-applied" : "--dismiss")} requires a URL argument.";
            }
        }

        return opts;
    }

    public static string Usage => string.Join('\n',
        "Usage:",
        "  dotnet run --project src/JobRadar.Console -- [--dry-run]",
        "  dotnet run --project src/JobRadar.Console -- --mark-applied <url> [--note \"text\"]",
        "  dotnet run --project src/JobRadar.Console -- --dismiss <url> [--note \"text\"]",
        "  dotnet run --project src/JobRadar.Console -- --list-pending");
}
