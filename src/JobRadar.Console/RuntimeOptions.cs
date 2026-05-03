namespace JobRadar.App;

public sealed class RuntimeOptions
{
    public bool DryRun { get; set; }
    public string RepoRoot { get; set; } = string.Empty;

    public static RuntimeOptions FromArgs(string[] args, string repoRoot) => new()
    {
        DryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)),
        RepoRoot = repoRoot,
    };
}
