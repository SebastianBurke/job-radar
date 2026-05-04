using JobRadar.App;
using JobRadar.App.Config;
using JobRadar.Core.Abstractions;
using JobRadar.Core.Config;
using JobRadar.Notify;
using JobRadar.Scoring;
using JobRadar.Sources;
using JobRadar.Sources.Internal;
using JobRadar.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var repoRoot = RepoPaths.FindRepoRoot();
var options = RuntimeOptions.FromArgs(args, repoRoot);

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

// Storage.
builder.Services.AddSingleton<IDedupStore>(sp => new SqliteStore(
    Path.Combine(repoRoot, "data", "seen.db"),
    sp.GetRequiredService<ILogger<SqliteStore>>()));

// Scorer.
builder.Services.AddSingleton(_ => new ClaudeScorerOptions
{
    ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty,
    PromptPath = Path.Combine(repoRoot, "prompts", "scoring-prompt.md"),
    CvPath = Path.Combine(repoRoot, "data", "cv.md"),
    EligibilityPath = Path.Combine(repoRoot, "data", "eligibility.md"),
    Concurrency = 2,
    MaxTokens = 1500,
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
var companies = host.Services.GetRequiredService<CompaniesConfig>();
var filters = host.Services.GetRequiredService<FiltersConfig>();
var pipeline = host.Services.GetRequiredService<Pipeline>();
var notifier = host.Services.GetRequiredService<INotifier>();

logger.LogInformation(
    "JobRadar starting (dry-run={DryRun}); {CompanyCount} companies configured, max {MaxCalls} scoring calls per run.",
    options.DryRun,
    companies.Companies.Count,
    filters.MaxScoringCallsPerRun);

var (entries, stats) = await pipeline.RunAsync();

await notifier.SendDigestAsync(entries);

logger.LogInformation(
    "Run complete in {Duration}. Fetched: {Fetched}. Filtered (kw/loc): {KwOut}/{LocOut}. Deduped: {Dup}. Scored: {Scored}. Dropped ineligible: {Ineligible}. Aborted (cost guard): {Aborted}.",
    stats.Duration,
    string.Join(", ", stats.FetchedPerSource.Select(kv => $"{kv.Key}={kv.Value}")),
    stats.FilteredOutByKeyword,
    stats.FilteredOutByLocation,
    stats.DedupedOut,
    stats.Scored,
    stats.DroppedIneligible,
    stats.Aborted);

// Cost estimate: Haiku 4.5 input ~$1/MTok, output ~$5/MTok. Each posting averages ~1.5k input, 200 output tokens.
var estUsd = stats.Scored * ((1500 / 1_000_000.0) * 1.0 + (200 / 1_000_000.0) * 5.0);
logger.LogInformation("Estimated Anthropic spend this run: ~${Cost:F4} ({Calls} calls).", estUsd, stats.Scored);

return 0;
