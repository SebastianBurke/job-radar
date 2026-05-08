# job-radar

A daily job-posting aggregator. Runs on a GitHub Actions cron, scores each posting against the candidate's CV via the Claude API, and emails a sorted digest of the best matches. Zero hosting cost.

## What it does

- Pulls postings from public ATS APIs (Greenhouse, Lever, Ashby, Workable) for a configured company list, plus aggregator feeds (RemoteOK, Remotive, WeWorkRemotely RSS, Hacker News "Who is hiring"), the Jobillico Quebec-area board, and any JSON snapshots dropped into `data/captured/` by an external headless-browser instance (see `HEADLESS-SOURCE-HANDOFF.md`).
- Filters out non-.NET roles and US-only postings before any API call.
- Sends survivors to Claude Haiku 4.5 for a fit score (1–10), eligibility verdict, top concern, and one-line pitch.
- Emails a daily digest grouped by score band: top matches (8–10), worth a look (5–7), sanity check (1–4).

The candidate reviews and applies manually — this bot only finds and ranks.

## How it works

```
GitHub Actions (cron 12:00 UTC)
        │
        ▼
JobRadar.Console
  1. Load config (companies.yml, filters.yml, sources.yml, cv.md, eligibility.md, scoring-prompt.md)
  2. Fetch postings from all IJobSource implementations
  3. Keyword filter — must hit a .NET keyword OR (broad term + tech context)
  4. Location filter — allow Canada/Spain/EU/Remote, deny US-only/H1B/clearance
  5. SQLite dedup against data/seen.db (SHA-256 of company|title|description[:200])
  6. Cache invalidation — compute scoring_inputs_hash over cv.md / eligibility.md /
     scoring-prompt.md / filters.yml; pending postings whose cached score was
     computed under a different hash are re-scored this run. Pass --rescore-all
     to force a flush regardless of the hash.
  7. ATS live-check — verify the posting is still listed on the underlying ATS
     (or, for aggregator sources, that the apply URL still resolves). 404s under
     `require_ok` are dropped before scoring; under `best_effort` they're logged
     and scored anyway. Verdicts cached in seen.db so dead URLs aren't re-fetched.
  8. Score via Claude Haiku — concurrency 2, retry on 429/5xx. Pre-scan applies
     a stack-tier modifier (+2 .NET-only, -2 mismatched-only, 0 polyglot) to the
     model's returned score, clamped to 1–10.
  9. Drop "ineligible" verdicts (US-only, fluent-French-required, etc.)
 10. Build inline-CSS HTML digest
 11. Send via Resend
 12. Commit updated data/seen.db
```

Each posting is also tagged with a `LocationConfidence` (Authoritative for ATS
sources, AggregatorOnly for aggregators) so the rubric can downgrade aggregator
"Remote / Worldwide" tags that frequently mistag geo-fenced roles.

## Stack

- .NET 8, C# (file-scoped namespaces, nullable enabled, async throughout)
- `Microsoft.Extensions.Hosting` for DI + config
- `IHttpClientFactory` for all HTTP
- `Microsoft.Data.Sqlite` for the dedup store
- xUnit for tests, real captured fixtures under `tests/JobRadar.Tests/Fixtures/`
- Raw `HttpClient` for Anthropic — no SDK

## Project layout

```
src/
  JobRadar.Console/    entry point, DI, pipeline orchestrator
  JobRadar.Core/       models + interfaces (IJobSource, IScorer, INotifier, IDedupStore)
  JobRadar.Sources/    one class per source, all implement IJobSource
  JobRadar.Scoring/    Claude Haiku scorer with retry/backoff
  JobRadar.Storage/    SQLite dedup store
  JobRadar.Notify/     Resend email + HTML digest renderer
tests/JobRadar.Tests/  xUnit tests, fixtures committed
config/                companies.yml, filters.yml, sources.yml
prompts/               scoring-prompt.md (loaded at runtime)
data/                  cv.md, eligibility.md, seen.db (all committed)
.github/workflows/     daily-scan.yml
```

## Quick start

Prerequisite: .NET 8 SDK.

```bash
# 1. Build and test
dotnet build
dotnet test

# 2. Local dry-run (no email sent, scorer falls back without API key)
dotnet run --project src/JobRadar.Console -- --dry-run

# 3. Local real run (need ANTHROPIC_API_KEY + Resend secrets)
export ANTHROPIC_API_KEY=sk-ant-...
export RESEND_API_KEY=re_...
export EMAIL_TO=you@example.com
export EMAIL_FROM=job-radar@yourdomain.com
dotnet run --project src/JobRadar.Console
```

For local development, `dotnet user-secrets` is the standard alternative to `export` — it stores secrets outside the repo so they can't be committed by accident.

## Configuration

| File | Purpose |
|------|---------|
| `config/companies.yml` | ATS-keyed company list. Each entry: `name`, `region`, `ats` (`greenhouse \| lever \| ashby \| workable \| unknown`), `token` (the slug used by the ATS API). |
| `config/filters.yml` | Two-tier keyword filter (`keywords_core` passes alone; `keywords_broad` only with a `tech_context_hint`), location allow/deny, max scoring calls per run. |
| `config/sources.yml` | Per-source params for aggregator sources: Remotive search terms, WeWorkRemotely RSS feed URLs, Jobillico search-term × location cross product, per-source `live_check` policy. Edit this when changing stack focus or geography. |
| `prompts/scoring-prompt.md` | Loaded at runtime; placeholders `{{cv}}`, `{{eligibility}}`, `{{posting.title}}` etc. are substituted before each Anthropic call. Has a `## System` and `## User` section split by `---`. |
| `data/cv.md` | The candidate CV that the scorer compares postings against. Plain markdown. |
| `data/eligibility.md` | The candidate's work-authorization declaration, injected into the prompt's eligibility-rules section. Edit this when authorization changes (or when sharing the bot with a friend). |
| `data/seen.db` | SQLite dedup store. Committed so dedup persists across CI runs. |

To resolve a company's ATS slug: hit each of the four ATS public endpoints with the suspected slug (e.g. `https://boards-api.greenhouse.io/v1/boards/{slug}/jobs?content=true`) and use whichever returns 200 with non-empty jobs. Check the company's careers page if the API probes don't match.

## Secrets

Set as **Repository Secrets** under `Settings → Secrets and variables → Actions`:

| Name | What |
|------|------|
| `ANTHROPIC_API_KEY` | Claude API key. Set a $10/month budget cap in the Anthropic console. |
| `RESEND_API_KEY` | Resend API key (free tier: 3k emails/month). |
| `EMAIL_TO` | Where the daily digest is sent. |
| `EMAIL_FROM` | Verified Resend sender domain, or `onboarding@resend.dev` for testing into your own Resend account. |

## CI / scheduling

`.github/workflows/daily-scan.yml`:
- Cron `0 12 * * *` (12:00 UTC = 07:00 ET / 08:00 EDT) plus `workflow_dispatch` for manual triggers.
- Steps: checkout → setup-dotnet 8 → restore → build → test → run scan → commit and push updated `data/seen.db`.
- Job timeout: 20 minutes.

Trigger manually from the **Actions** tab → `daily-scan` → `Run workflow`.

## Cost guard

Two layers:
1. **Anthropic console budget** — manual $10/month cap.
2. **Per-run cap** — `max_scoring_calls_per_run` in `filters.yml` (default 200). The pipeline aborts before any API call if survivors after dedup exceed this.

Per-run cost is typically $0.05–0.15 for 30–60 postings.

## Forking for a different person or stack

The bot's calibration lives in six files. To repoint it at a different candidate or a different stack, fork the repo and edit those files only — no `.cs` changes needed.

| File | What to change |
|------|----------------|
| `data/cv.md` | The new candidate's CV. The scorer rereads it on every run. |
| `data/eligibility.md` | The new candidate's work-authorization declaration (e.g. "US citizen, no Canadian auth"). Injected into the prompt at render time. |
| `config/companies.yml` | Their target companies; resolve each one's ATS slug as described above. |
| `config/filters.yml` | Their stack keywords (`keywords_core`, `keywords_broad`, `tech_context_hints`) and target locations. |
| `config/sources.yml` | Aggregator search terms and RSS feed URLs — adjust away from .NET defaults if needed. |
| `prompts/scoring-prompt.md` | Only edit if you want a different rubric (most forks won't). |

Plus: set the four GitHub secrets in their fork (`ANTHROPIC_API_KEY`, `RESEND_API_KEY`, `EMAIL_TO`, `EMAIL_FROM`), and delete + recommit `data/seen.db` so the first run scores everything fresh.

To **widen your own search** (same person, broader scope) the recipe is the same minus the secrets and seen.db reset — just edit the files in this repo.

## Adding a new source

1. Implement `IJobSource` in `src/JobRadar.Sources/`. Use `IHttpClientFactory` and `HostRateLimiter`.
2. Register in `src/JobRadar.Console/Program.cs` as `IJobSource`.
3. Capture a real response under `tests/JobRadar.Tests/Fixtures/<Source>/`. Trim to 2–3 entries to keep the fixture small.
4. Add a parser test in `tests/JobRadar.Tests/Sources/` using `StaticHttpHandler.FromFixture(...)`.

Hard rules:
- All HTTP calls send `User-Agent: JobRadar/1.0 (personal-bot)`.
- Rate-limit to ~1 req/sec per host; back off on 429.
- Never scrape LinkedIn — use LinkedIn's native job alerts to a separate inbox folder instead.
- If a site exposes a public API or RSS, always prefer the structured source.
- Don't auto-submit applications. This bot only finds and ranks.

## Troubleshooting

**Email arrived with everything scored 1/10** — the scorer is returning the fallback shape. Look at the `Scorer error: <reason>` line under the concern on each fallback row. Common reasons:
- `Anthropic HTTP 429.` after retries — drop `Concurrency` further or check Anthropic tier limits.
- `JSON parse error: …` — model returned non-JSON; tweak the prompt or the `ExtractJsonObject` heuristic in `ClaudeScorer.cs`.
- `Anthropic HTTP 5xx` — Anthropic-side outage; usually transient.
- `Anthropic request error.` — network or timeout.

**Workflow ran but no email** — almost always dedup. Check the run's `Run scan` log for `Scored: 0`. To force a fresh scoring pass, delete and recommit `data/seen.db`.

**Workflow timeout** — fetch is serialized across 8 sources, each with 1s/host rate limiting. Bump `timeout-minutes` in the workflow, or parallelize source fetching.

**ATS source returns 0 jobs** — slug is probably wrong or the company moved off that ATS. Re-probe the four endpoints; mark `ats: unknown` if none populated.

## Known limitations

- **Workable** v1 widget API has no per-job descriptions; `WorkableSource.cs` synthesizes one from job + company metadata, which limits scoring quality on Workable rows.
- **GitLab Greenhouse** board returns 200 jobs (paginated cap); some roles may be missed until pagination is implemented.
- **11 of 20 configured companies** in `config/companies.yml` have working ATS slugs. The other 9 use non-Big-4 systems (Workday, Teamtailor, Njoyn, custom WordPress) and currently contribute 0 postings — see the per-entry notes.
- **GcJobs / Emplois GC** is intentionally deferred (parsing strategy + robots.txt review pending).
- **Source fetching is serial across hosts**; total fetch time ≈ sum of per-source latencies. Parallelizing the fan-out is a known follow-up that would cut a typical run from ~5 min to ~1 min.

## See also

- `CLAUDE.md` — short orientation for AI-assisted contributors and the hard rules.
- `SPEC.md` — full design spec, scoring contract, acceptance criteria.
- `BUILD-PROMPTS.md` — historical: the original step-by-step build sequence used to bootstrap the repo. Not a current setup guide.
- `HEADLESS-SOURCE-HANDOFF.md` — brief for a sibling Playwright instance; describes the JSON schema for `data/captured/*.json` files that fold JS-rendered sources (GC Jobs, Workday tenants, etc.) into this radar's pipeline.
