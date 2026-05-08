using JobRadar.App;
using JobRadar.App.Config;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;
using JobRadar.Notify;
using JobRadar.Scoring;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Sources.LiveCheck;
using JobRadar.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var repoRoot = RepoPaths.FindRepoRoot();
var options = RuntimeOptions.FromArgs(args, repoRoot);

if (!string.IsNullOrEmpty(options.UsageError))
{
    Console.Error.WriteLine(options.UsageError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(RuntimeOptions.Usage);
    return 2;
}

// Modes that don't need the full pipeline / scorer / notifier short-circuit early.
if (options.Mode is RunMode.MarkApplied or RunMode.Dismiss)
{
    var appliedYamlPath = Path.Combine(repoRoot, "data", "applied.yml");
    var bucket = options.Mode == RunMode.MarkApplied ? "applied" : "dismissed";
    AppliedYamlStore.Append(appliedYamlPath, bucket, options.TargetUrl!, options.Note, DateTimeOffset.UtcNow);
    Console.WriteLine($"Recorded {bucket}: {options.TargetUrl}");
    Console.WriteLine($"Wrote {appliedYamlPath}. Commit and push so the next CI run picks it up.");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => ConfigLoader.LoadCompanies(repoRoot));
builder.Services.AddSingleton(_ => ConfigLoader.LoadFilters(repoRoot));
builder.Services.AddSingleton(_ => ConfigLoader.LoadSources(repoRoot));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<HostRateLimiter>();

// Job sources (registered as IJobSource so the pipeline gets all of them).
builder.Services.AddSingleton<IJobSource, GreenhouseSource>();
builder.Services.AddSingleton<IJobSource, LeverSource>();
builder.Services.AddSingleton<IJobSource, AshbySource>();
builder.Services.AddSingleton<IJobSource, WorkableSource>();
builder.Services.AddSingleton<IJobSource, RemoteOKSource>();
builder.Services.AddSingleton<IJobSource, RemotiveSource>();
builder.Services.AddSingleton<IJobSource, WeWorkRemotelySource>();
builder.Services.AddSingleton<IJobSource, HackerNewsHiringSource>();
builder.Services.AddSingleton<IJobSource, JobillicoSource>();

// ATS live-check.
builder.Services.AddSingleton<IAtsLiveChecker, AtsLiveChecker>();

// Storage.
builder.Services.AddSingleton<IDedupStore>(sp => new SqliteStore(
    Path.Combine(repoRoot, "data", "seen.db"),
    sp.GetRequiredService<ILogger<SqliteStore>>()));

// Scorer.
builder.Services.AddSingleton(sp =>
{
    var filters = sp.GetRequiredService<FiltersConfig>();
    return new ClaudeScorerOptions
    {
        ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty,
        PromptPath = Path.Combine(repoRoot, "prompts", "scoring-prompt.md"),
        CvPath = Path.Combine(repoRoot, "data", "cv.md"),
        EligibilityPath = Path.Combine(repoRoot, "data", "eligibility.md"),
        Concurrency = 2,
        MaxTokens = 1500,
        StackSignals = filters.StackSignals,
        TitleSignals = filters.TitleSignals,
    };
});
builder.Services.AddSingleton<IScorer, ClaudeScorer>();

// Notifier.
builder.Services.AddSingleton(_ => new ResendOptions
{
    ApiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY") ?? string.Empty,
    From = Environment.GetEnvironmentVariable("EMAIL_FROM") ?? string.Empty,
    To = Environment.GetEnvironmentVariable("EMAIL_TO") ?? string.Empty,
    DryRun = options.DryRun,
});
builder.Services.AddSingleton<INotifier, ResendEmailNotifier>();

// Pipeline.
builder.Services.AddSingleton<Pipeline>();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();

if (options.Mode == RunMode.ListPending)
{
    var dedup = host.Services.GetRequiredService<IDedupStore>();
    await dedup.InitializeAsync();
    var pending = await dedup.ListPendingAsync();
    if (pending.Count == 0)
    {
        Console.WriteLine("No pending postings.");
        return 0;
    }
    foreach (var p in pending.OrderByDescending(p => p.CachedScore?.MatchScore ?? 0))
    {
        var score = p.CachedScore?.MatchScore.ToString() ?? "?";
        Console.WriteLine($"[{score}/10] {p.Posting.Company} · {p.Posting.Title} ({p.Posting.Location})");
        Console.WriteLine($"        first seen {p.SeenAt:yyyy-MM-dd}, last seen {p.LastSeenAt:yyyy-MM-dd}");
        Console.WriteLine($"        {p.Posting.Url}");
    }
    Console.WriteLine();
    Console.WriteLine($"{pending.Count} pending posting(s).");
    return 0;
}

var companies = host.Services.GetRequiredService<CompaniesConfig>();
var filters = host.Services.GetRequiredService<FiltersConfig>();
var pipeline = host.Services.GetRequiredService<Pipeline>();
var notifier = host.Services.GetRequiredService<INotifier>();

logger.LogInformation(
    "JobRadar starting (dry-run={DryRun}); {CompanyCount} companies configured, max {MaxCalls} scoring calls per run, pending grace {Grace}d.",
    options.DryRun,
    companies.Companies.Count,
    filters.MaxScoringCallsPerRun,
    filters.PendingGraceDays);

var (entries, stats) = await pipeline.RunAsync();

await notifier.SendDigestAsync(entries);

logger.LogInformation(
    "Run complete in {Duration}. Fetched: {Fetched}. Filtered (kw/loc): {KwOut}/{LocOut}. Carried pending: {Pending}. Resurrected: {Resurrected}. Expired: {Expired}. Marked from yaml: {Yaml}. Scored: {Scored}. Dropped ineligible: {Ineligible}. Aborted (cost guard): {Aborted}.",
    stats.Duration,
    string.Join(", ", stats.FetchedPerSource.Select(kv => $"{kv.Key}={kv.Value}")),
    stats.FilteredOutByKeyword,
    stats.FilteredOutByLocation,
    stats.Pending,
    stats.Resurrected,
    stats.Expired,
    stats.MarkedFromYaml,
    stats.Scored,
    stats.DroppedIneligible,
    stats.Aborted);

// Cost estimate: Haiku 4.5 input ~$1/MTok, output ~$5/MTok. Each posting averages ~1.5k input, 200 output tokens.
var estUsd = stats.Scored * ((1500 / 1_000_000.0) * 1.0 + (200 / 1_000_000.0) * 5.0);
logger.LogInformation("Estimated Anthropic spend this run: ~${Cost:F4} ({Calls} calls).", estUsd, stats.Scored);

return 0;
