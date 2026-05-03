namespace JobRadar.App;

public static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("JOBRADAR_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return env;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JobRadar.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate JobRadar.sln; set JOBRADAR_REPO_ROOT to the repo root.");
    }
}
