using System.Security.Cryptography;
using System.Text;

namespace JobRadar.Scoring;

/// <summary>
/// Computes a SHA-256 fingerprint of every file the scorer's behaviour depends
/// on. The pipeline persists this fingerprint alongside each cached score and
/// invalidates the cache whenever the fingerprint changes — so a cv.md or
/// scoring-prompt.md edit re-prices the backlog automatically on the next run
/// instead of leaving stale scores frozen in <c>seen.db</c>.
/// </summary>
public static class ScoringInputsHasher
{
    /// <summary>
    /// The four files whose byte content determines the scorer's verdict for
    /// any given posting. Order is stable so the hash is reproducible.
    /// </summary>
    public static IReadOnlyList<string> DefaultPaths(string repoRoot) => new[]
    {
        Path.Combine(repoRoot, "data", "cv.md"),
        Path.Combine(repoRoot, "data", "eligibility.md"),
        Path.Combine(repoRoot, "prompts", "scoring-prompt.md"),
        Path.Combine(repoRoot, "config", "filters.yml"),
    };

    public static string Compute(string repoRoot) => Compute(DefaultPaths(repoRoot));

    public static string Compute(IEnumerable<string> filePaths)
    {
        using var sha = SHA256.Create();
        foreach (var path in filePaths)
        {
            // Marker keeps the boundaries between files explicit so swapping content
            // between (say) cv.md and eligibility.md still produces a different hash.
            var marker = Encoding.UTF8.GetBytes($"\n---{Path.GetFileName(path)}---\n");
            sha.TransformBlock(marker, 0, marker.Length, null, 0);

            if (File.Exists(path))
            {
                var content = File.ReadAllBytes(path);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
            // Missing file: the marker alone contributes — still deterministic.
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
