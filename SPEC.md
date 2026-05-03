# job-radar — build spec

A daily job-posting aggregator. Runs on a GitHub Actions cron, scores each posting against the candidate's CV via the Claude API, sends a daily digest by email.

## Goals

- Find all relevant .NET / fullstack roles in (Montréal | Ottawa | Spain | Remote-EU/CA-friendly) without manual board hunting.
- Auto-exclude US-only and otherwise ineligible postings.
- Pre-rank by fit so the candidate reviews only the top 5–10 each morning.
- Zero hosting cost.

## Non-goals

- Auto-submitting applications.
- Replacing LinkedIn for direct outreach.
- Application tracking beyond "seen / not seen". (Future: optional Notion/Sheets export.)

## Architecture

```
                ┌─────────────────────────────────────────┐
GitHub Actions─▶│ JobRadar.Console (.NET 8)                │
  (cron daily)  │                                          │
                │ 1. Load config (companies.yml, filters)  │
                │ 2. Fan out to N IJobSource workers       │──▶ ATS APIs / RSS
                │ 3. Hard-filter (keyword + location)      │
                │ 4. Dedupe via SQLite (data/seen.db)      │
                │ 5. Score survivors via Claude API        │──▶ api.anthropic.com
                │ 6. Drop ineligible postings              │
                │ 7. Build HTML digest                     │
                │ 8. Send via Resend                       │──▶ resend.com
                │ 9. Commit updated seen.db                │
                └─────────────────────────────────────────┘
```

## Project structure

```
job-radar/
├── .github/workflows/daily-scan.yml
├── src/
│   ├── JobRadar.Console/
│   │   ├── Program.cs
│   │   └── Pipeline.cs
│   ├── JobRadar.Core/
│   │   ├── Models/      (JobPosting, ScoringResult, DigestEntry)
│   │   └── Abstractions/ (IJobSource, IScorer, INotifier)
│   ├── JobRadar.Sources/
│   ├── JobRadar.Scoring/
│   ├── JobRadar.Storage/
│   └── JobRadar.Notify/
├── tests/JobRadar.Tests/
├── config/
│   ├── companies.yml
│   └── filters.yml
├── prompts/
│   └── scoring-prompt.md
├── data/
│   ├── cv.md
│   └── seen.db
├── CLAUDE.md
├── SPEC.md
├── BUILD-PROMPTS.md
└── README.md
```

## Sources to implement (priority order)

### Tier 1 — public structured APIs (no scraping)

| # | Source | Endpoint pattern | Notes |
|---|--------|------------------|-------|
| 1 | Greenhouse | `https://boards-api.greenhouse.io/v1/boards/{token}/jobs?content=true` | Iterate over tokens in companies.yml |
| 2 | Lever | `https://api.lever.co/v0/postings/{company}?mode=json` | |
| 3 | Ashby | `https://api.ashbyhq.com/posting-api/job-board/{org}` | |
| 4 | Workable | `https://apply.workable.com/api/v3/accounts/{account}/jobs` | |
| 5 | RemoteOK | `https://remoteok.com/api` | Single endpoint, returns array |
| 6 | Remotive | `https://remotive.com/api/remote-jobs?search={keyword}` | |
| 7 | GetonBoard | `https://www.getonbrd.com/api/v0/categories/programming/jobs` | Spain + LatAm |

### Tier 2 — RSS feeds

| # | Source | Notes |
|---|--------|-------|
| 8 | We Work Remotely | Per-category RSS |
| 9 | Working Nomads | RSS |
| 10 | Tecnoempleo | Spain tech, RSS |

### Tier 3 — special handling

| # | Source | Notes |
|---|--------|-------|
| 11 | GC Jobs / Emplois GC | Federal Canada. Parse search results page. Respect robots.txt. Hold for confirmation before implementing. |
| 12 | Hacker News "Who is hiring" | Fetch the current month's thread via the HN Firebase API; parse top-level comments. |

### Out of scope

- LinkedIn scraping. Use LinkedIn's native job alerts; that email digest can sit in a separate inbox folder.
- InfoJobs scraping. Defer until a clean integration path exists.

## Filter pipeline

For each posting, in this order:

1. **Keyword filter (regex)** — must match `\b(\.net|c#|dotnet|asp\.net|blazor|full[\- ]?stack|software (engineer|developer))\b`. Skip otherwise.
2. **Location filter** — keep if location string matches: `montreal|montréal|ottawa|quebec|québec|ontario|spain|españa|madrid|barcelona|remote`. Drop if it explicitly says "US only", "United States only", "must reside in the US".
3. **Dedupe** — SHA-256 hash of `(company || "|" || title || "|" || description.Substring(0, 200))`. If hash in `seen.db`, skip.
4. **Score via Claude API** — only postings that survive 1–3 hit the API.
5. **Eligibility filter** — drop postings the API marks `"ineligible"`.

## Scoring contract

The scorer returns this JSON for each posting (see `prompts/scoring-prompt.md`):

```json
{
  "match_score": 8,
  "eligibility": "eligible",
  "eligibility_reason": "Posting allows EU-based remote candidates.",
  "top_3_matched_skills": ["C#", "Azure DevOps", "Angular"],
  "top_concern": "Requires 5+ years of senior backend; candidate has 3+.",
  "estimated_seniority": "mid",
  "language_required": "english",
  "salary_listed": "€55k–€70k",
  "remote_policy": "remote",
  "one_line_pitch": "Strong .NET + Angular fit at a Spain-friendly remote shop with explicit EU eligibility."
}
```

Use **Claude Haiku 4.5** (`claude-haiku-4-5-20251001`) for cost. Estimate ~$0.001–0.01 per posting.

Eligibility values: `"eligible" | "ineligible" | "ambiguous"`. Drop `ineligible` from the digest. Include `ambiguous` but flag visually.

## Digest output

Daily HTML email, sorted descending by `match_score`. Three groups:

- ⭐ **Top matches** (score 8–10) — review first
- ✅ **Worth a look** (score 5–7)
- 📋 **Sanity check** (score 1–4) — collapsed by default; presence helps tune the keyword filter

Each entry: title, company, location, remote_policy, score, one_line_pitch, salary if listed, top_concern, direct apply URL.

Subject line: `[job-radar] {N} new postings — {top_score} top match`.

## Configuration files

### `config/companies.yml`

Each company entry: `{ name, ats, token, region, notes? }`.
- `ats`: `greenhouse | lever | ashby | workable`
- `token`: the slug used in the ATS API
- `region`: `ca-mtl | ca-ott | ca | es-mad | es-bcn | es | global | remote`

### `config/filters.yml`

```yaml
keywords_required: [".net", "c#", "dotnet", "asp.net", "blazor", "full stack", "fullstack", "software engineer", "software developer"]
location_allow: ["montreal", "montréal", "ottawa", "quebec", "québec", "ontario", "spain", "españa", "madrid", "barcelona", "remote"]
location_deny_phrases: ["us only", "united states only", "must reside in the us", "us citizens only", "h1b", "tn visa"]
```

## CI/CD

`.github/workflows/daily-scan.yml`:

- Triggers: `schedule: cron: '0 12 * * *'` (07:00 ET) + `workflow_dispatch`
- Steps:
  1. checkout
  2. setup-dotnet 8
  3. dotnet run with secrets
  4. commit & push updated `data/seen.db`
- Secrets required:
  - `ANTHROPIC_API_KEY`
  - `RESEND_API_KEY`
  - `EMAIL_TO`
  - `EMAIL_FROM` (verified Resend sender)

## Cost guard

- Set Anthropic API monthly budget alert at $10 in console.
- Runtime guard: if API calls in one run > 200, abort with an error log.

## Acceptance criteria (MVP done)

- [ ] At least 5 sources implemented and tested with captured real responses
- [ ] One end-to-end CI run produces a digest email
- [ ] Dedupe verified across two consecutive runs
- [ ] All US-only postings excluded in scoring spot-check
- [ ] Total runtime < 2 minutes
- [ ] Total Anthropic API spend per run < $0.50
