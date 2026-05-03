# Build sequence — prompts for Claude Code

Paste these into Claude Code one at a time. Verify the output of each before moving on. Use plan mode (`/plan`) for the larger ones — let it produce a plan, review, then run.

---

## Prerequisites (do these yourself before opening Claude Code)

1. Install Claude Code: `npm install -g @anthropic-ai/claude-code` (Node 18+ required).
2. Get an Anthropic API key from `console.claude.com`. Set a $10 monthly budget cap.
3. Get a Resend API key (free tier covers 3k emails/month). Verify a sender domain or start with their onboarding sender for testing.
4. Create a private GitHub repo: `job-radar`. Clone locally.
5. Drop these files into the repo root before opening Claude Code:
   - `CLAUDE.md`
   - `SPEC.md`
   - `BUILD-PROMPTS.md` (this file)
   - `prompts/scoring-prompt.md`
   - `config/companies.yml`
   - `config/filters.yml`
   - `data/cv.md`
6. From the repo root: `claude`. If it suggests `/init`, accept — but don't let it overwrite the CLAUDE.md you already wrote.

---

## Build prompts

### 1. Bootstrap the solution

> Read `CLAUDE.md` and `SPEC.md`. Then create the .NET 8 solution structure exactly as described in SPEC.md "Project structure". Set up `Microsoft.Extensions.Hosting` with DI, structured logging, and configuration binding from `appsettings.json` plus environment variables. Create empty interfaces `IJobSource`, `IScorer`, `INotifier` in `JobRadar.Core/Abstractions/`. Stub `Program.cs` to load config, log a startup message, and exit. `dotnet build` must succeed from the repo root.

### 2. Domain models

> Implement the domain models in `JobRadar.Core/Models/`: `JobPosting`, `ScoringResult`, `EligibilityVerdict`, `DigestEntry`. Use C# records. Add `System.Text.Json` attributes where needed. Match the scoring contract in SPEC.md exactly.

### 3. SQLite dedup store

> Implement `JobRadar.Storage/SqliteStore.cs` using `Microsoft.Data.Sqlite`. It should: open `data/seen.db` (create if missing), expose `Task<bool> HasSeenAsync(string hash)` and `Task MarkSeenAsync(string hash, JobPosting posting, DateTimeOffset seenAt)`, store full posting metadata for audit, and use a single `seen` table. Run a migration on startup. Add unit tests using a temp file path.

### 4. First source — Greenhouse

> Implement `JobRadar.Sources/GreenhouseSource.cs`. Read company tokens from `config/companies.yml` where `ats: greenhouse`. Call `https://boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true` for each. Yield `JobPosting` objects. Use `IHttpClientFactory`. Send `User-Agent: JobRadar/1.0`. Rate limit 1 req/sec per host. Write fixture-based unit tests in `tests/JobRadar.Tests/Sources/GreenhouseSourceTests.cs` using a captured real response from one company.

### 5. Remaining sources (one at a time)

> Implement these in this order, each with a fixture test before moving to the next: `LeverSource`, `AshbySource`, `WorkableSource`, `RemoteOKSource`, `RemotiveSource`, `WeWorkRemotelySource` (RSS), `HackerNewsHiringSource` (Firebase API). Follow the same pattern as `GreenhouseSource`. Stop and ask me before tackling `GcJobsSource` — that one needs a discussion about robots.txt and parsing strategy.

### 6. Scorer

> Implement `JobRadar.Scoring/ClaudeScorer.cs`. At construction, load `prompts/scoring-prompt.md` and `data/cv.md`. Expose `Task<ScoringResult> ScoreAsync(JobPosting posting)`. Call the Anthropic Messages API directly with `HttpClient`: POST `https://api.anthropic.com/v1/messages`, headers `x-api-key` (from env) and `anthropic-version: 2023-06-01`, model `claude-haiku-4-5-20251001`, max_tokens 600. Parse the response as strict JSON; on parse failure, log and return a default low-score result with `eligibility = "ambiguous"`. Add an overload `IAsyncEnumerable<ScoringResult> ScoreManyAsync(IEnumerable<JobPosting>)` with concurrency limit 5.

### 7. Filter pipeline

> Implement `JobRadar.Console/Pipeline.cs`. Order: keyword filter → location filter → dedupe (via `SqliteStore`) → score (via `ClaudeScorer`) → eligibility filter. The first three are fast and cheap; only survivors hit the API. Pull keyword and location lists from `config/filters.yml`. Add a hard cap: abort the run if survivors after dedupe exceed 200 (cost guard).

### 8. Email notifier

> Implement `JobRadar.Notify/ResendEmailNotifier.cs` using the Resend HTTP API (`https://api.resend.com/emails`). Build an HTML digest grouped by score band (8–10, 5–7, 1–4) per SPEC.md "Digest output". Each entry shows title, company, location, score, one-line pitch, top concern, and apply URL. Inline CSS only (no external stylesheets — many email clients strip them). Subject: `[job-radar] {N} new postings — {top_score} top match`. Test with `--dry-run` first to print to stdout, then send a real email.

### 9. Wire it up

> Update `Program.cs` to: load config → register all sources via DI → run pipeline → score survivors → build digest → send email. Add a `--dry-run` flag that skips sending and prints to stdout. At end of run, log: postings fetched per source, postings filtered out at each stage, postings scored, estimated API cost (input + output tokens × Haiku rate).

### 10. CI

> Create `.github/workflows/daily-scan.yml`. Cron `0 12 * * *` (07:00 ET). Add `workflow_dispatch` for manual runs. Steps: checkout (with token allowing push), setup-dotnet 8, dotnet run with secrets `ANTHROPIC_API_KEY` / `RESEND_API_KEY` / `EMAIL_TO` / `EMAIL_FROM` injected as env vars, then commit & push updated `data/seen.db` if it changed. Use a paths-ignore on the workflow itself to avoid loops.

### 11. Smoke test

> Run end-to-end locally: `dotnet run --project src/JobRadar.Console -- --dry-run`. Verify: at least one posting per source, dedupe works on a second consecutive run, scoring returns valid JSON, digest renders cleanly. Then trigger the workflow manually with `gh workflow run daily-scan.yml` and confirm the email arrives.

---

## Watch out for

- **Claude Code overengineering.** If it suggests microservices, a service mesh, or message queues, push back. This is one console app on a daily cron.
- **SDK drift.** It may try to install the official Anthropic .NET SDK. Raw `HttpClient` is simpler and has no dependency churn. Tell it to stick with `HttpClient`.
- **Polly everywhere.** Polly is fine for HttpClient retries. Don't let it wrap the whole pipeline in resilience policies — overkill.
- **Stacked broken steps.** Make sure each step compiles and tests pass before the next prompt. Don't let it run 4 prompts ahead of green.
- **Auto-updating CLAUDE.md.** Auto-memory will try to add things. Review `/memory` periodically; delete anything that's noise.
- **Secret leakage.** Never let it log API responses raw — they may contain salary info or other sensitive data you don't want in CI logs. Log structured fields only.
