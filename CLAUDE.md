# job-radar

A daily job-posting aggregator that runs on a GitHub Actions cron, scores postings against the candidate's CV via the Claude API, and emails a sorted digest. Zero hosting cost. The candidate submits applications manually after review.

For full requirements and the build sequence, see `SPEC.md` and `BUILD-PROMPTS.md`. Treat those as the authoritative source — this file is just orientation.

## Stack

- .NET 8, C# (nullable enabled, file-scoped namespaces, async everywhere)
- DI and config via `Microsoft.Extensions.Hosting`
- HTTP via `IHttpClientFactory`
- Storage: SQLite via `Microsoft.Data.Sqlite`
- Tests: xUnit, fixtures committed under `tests/Fixtures/`
- Logging: `Microsoft.Extensions.Logging` with structured fields
- One entry point: `JobRadar.Console`

## Layout

```
src/
  JobRadar.Console/    entry point + pipeline
  JobRadar.Core/       models + interfaces (IJobSource, IScorer, INotifier)
  JobRadar.Sources/    one class per job source
  JobRadar.Scoring/    Claude API scorer
  JobRadar.Storage/    SQLite dedup store
  JobRadar.Notify/     email digest sender
prompts/               runtime prompt templates
config/                YAML config (companies, filters)
data/                  cv.md + seen.db (committed)
```

## Conventions

- Each job source implements `IJobSource` in `JobRadar.Sources/`. Add a new source = add a class + register in DI + add a fixture test.
- The scoring prompt lives in `prompts/scoring-prompt.md` and is loaded at runtime. Do not hardcode prompt text in C#.
- All HTTP calls send `User-Agent: JobRadar/1.0 (personal-bot)`.
- Rate limit: max 1 req/sec per host; back off on 429.
- Secrets only via env vars. Local: `dotnet user-secrets`. CI: GitHub Actions secrets. Never commit a key.
- Use raw `HttpClient` for the Anthropic API. Do not pull in an SDK; this stays simple.

## Hard rules

- Do NOT scrape LinkedIn. Use LinkedIn's native job alerts instead.
- Do NOT scrape any site that exposes a public API or RSS — always prefer the structured source.
- Do NOT auto-submit applications. This bot only finds and ranks.
- Run cost ceiling: abort if Anthropic API calls in a single run exceed 200.

## Useful commands

- Build: `dotnet build`
- Run locally (no email sent): `dotnet run --project src/JobRadar.Console -- --dry-run`
- Test: `dotnet test`
- Trigger CI manually: `gh workflow run daily-scan.yml`
