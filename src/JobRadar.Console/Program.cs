using JobRadar.Console;
using JobRadar.Console.Config;
using JobRadar.Core.Config;
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
builder.Services.AddHttpClient();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var companies = host.Services.GetRequiredService<CompaniesConfig>();
var filters = host.Services.GetRequiredService<FiltersConfig>();

logger.LogInformation(
    "JobRadar starting (dry-run={DryRun}); {CompanyCount} companies configured, {KeywordCount} required keywords, max {MaxCalls} scoring calls per run.",
    options.DryRun,
    companies.Companies.Count,
    filters.KeywordsRequired.Count,
    filters.MaxScoringCallsPerRun);

await Task.CompletedTask;
return 0;
