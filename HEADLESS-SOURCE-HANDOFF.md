# Handoff: headless-browser sources for job-radar

This brief is for a sibling Claude instance (dispatch + cowork) that has Playwright running locally on the user's machine. The job-radar daily digest in this repo runs on GitHub Actions with curl-only HTTP and can't render JavaScript SPAs. Dispatch can. This document describes:

1. What the radar already covers (so dispatch doesn't duplicate effort).
2. What the radar tried to add and why it was blocked (so dispatch knows the leverage points).
3. The exact JSON schema dispatch must produce for the radar to integrate captured postings into the daily digest.
4. Per-source capture instructions, in priority order.

---

## The radar's current coverage (don't duplicate)

These sources work today, no headless rendering needed:

| Source | Mechanism |
|---|---|
| Greenhouse, Lever, Ashby, Workable | Documented public ATS JSON APIs. `config/companies.yml` has the slugs. |
| RemoteOK | Single JSON endpoint at `remoteok.com/api`. |
| Remotive | JSON API at `remotive.com/api/remote-jobs?search=…`. |
| WeWorkRemotely | Per-category RSS feeds. |
| HackerNews "Who is hiring" | Algolia + Firebase APIs. |
| Jobillico | Server-rendered HTML at `jobillico.com/search-jobs`. |

`data/applied.yml` is the user's "I've handled this" list — already-applied or dismissed URLs. The radar reconciles against that on every run; dispatch doesn't need to filter against it.

---

## What's blocked and why

| Tier | Source | Status | Probe result |
|---|---|---|---|
| **1** | **GC Jobs** (`emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page2440`) | ❌ JS-rendered SPA | Every URL I tried (`page2440`, `page1800`, form-submit with filters) returns the bare shell `"The page is being updated. Please wait..."` with zero listings in the served HTML. Listings populate via XHR after JS executes. |
| **1** | **Job Bank** (`jobbank.gc.ca/jobsearch`) | ❌ JS-rendered | Same shell pattern — the search UI is client-rendered. The Open Government Portal has a *monthly CSV dump* (`open.canada.ca/data/...job-bank-open-data...csv`) but it's 30+ days stale, useless for a daily radar. The XML feed exists but is partner-only (requires Job Bank approval). |
| **2** | **Workday** (`<tenant>.<shard>.myworkdayjobs.com`) | ❌ POST-only API + SPA | Listings come from `POST /wday/cxs/<tenant>/<board>/jobs` with a JSON payload. Public landing pages (e.g. `trendmicro.wd1.myworkdayjobs.com/External`) return 500 to bot UAs and are SPA shells anyway. Affects every large Ottawa-area enterprise: Cisco, Trend Micro, Ciena, BlackBerry, Nokia, Ericsson, IBM Canada, Accenture. |
| **3** | **Jobboom** (`jobboom.com/en/jobs`) | ⚠️ HTML but unclear filter syntax | Server-rendered HTML, but the filter URL params I guessed returned generic non-software results (RONA cashier postings instead of developer roles). The actual filter param names need DevTools-level discovery. |
| **3** | **Magnet** (`magnet.today`) | ❓ untested | Status unknown — may be defunct. Worth a quick check; skip if 404. |
| **4** | **Mitel / Kinaxis / Calian / Klipfolio / You.i** | ❓ untested, likely SPAs | Each is a different bespoke ATS or SuccessFactors tenant. Per `config/companies.yml` notes, most are custom WordPress portals or SuccessFactors. Modern enough that they're probably JS-rendered. |
| **5** | **Wellfound / Work at a Startup** | ❌ API path 404 / login wall | The `workatastartup.com/api/jobs/search` URL pattern returned 404. Wellfound (formerly AngelList) gates most listings behind login. |
| **5** | **JustRemote / Remoters / dotnetjobs** | ⚠️ partial | JustRemote landing page returned promotional content with no list rows. Remoters / dotnetjobs untested. |

**The pattern is uniform**: every modern enterprise job portal has moved to client-rendered SPAs. The radar's existing sources work because they're either documented APIs or pre-SPA static HTML. The next wave needs a browser.

---

## Priority for dispatch (highest leverage first)

Tier 1 (GC Jobs) is the single highest-leverage win. The user has 3 years of paid Service Canada experience on canada.ca search infrastructure — federal IT-stream postings are the band where their honest profile is competitive, and the radar misses the channel where their strongest credential is the literal job description. Tier 2 (Workday) is the next biggest reach gap by volume.

Tier 3 (Jobboom/Magnet), Tier 4 (Ottawa companies), Tier 5 (specialised boards) are smaller wins, lower priority.

If dispatch can only do one tier: **do Tier 1**.

---

## Integration: how to feed data back to the radar

Dispatch writes a JSON file per source under `data/captured/<source>.json` in this repo (one file per source, overwritten each capture; no need for date-stamped filenames). The radar's `CapturedJsonSource` reads every file matching `data/captured/*.json` on each cron run and folds the postings into the same dedup → live-check → score pipeline. The radar's existing `JobPosting` fingerprint dedupes against any duplicates already coming in via other channels, so there's no risk of double-scoring.

**Cadence**: daily is fine. The radar runs at 12:00 UTC; dispatch should commit fresh captures before that. Stale captures (>7 days old per `captured_at`) trigger a warning in the radar's logs but still get used — they just stop being useful when the underlying postings expire.

**Commit + push** the captured files on dispatch's schedule. The next cron picks them up automatically.

### JSON schema (required)

```json
{
  "captured_at": "2026-05-08T20:00:00Z",
  "source": "gcjobs",
  "postings": [
    {
      "ats_id": "1234567",
      "title": "Web Developer",
      "company": "Service Canada",
      "location": "Ottawa, Ontario, Canada",
      "url": "https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page1801?selectionProcessNumber=2026-CSD-EA-...",
      "description": "Full job description text, HTML-stripped, multi-line OK.",
      "posted_at": "2026-05-01",
      "department": "Service Canada",
      "metadata": {
        "classification": "IT-02",
        "language_requirement": "BBB",
        "close_date": "2026-05-22"
      }
    }
  ]
}
```

**Field reference**:

| Field | Required | Notes |
|---|---|---|
| `captured_at` | ✅ | ISO 8601 UTC. Radar uses this for staleness detection. |
| `source` | ✅ | Short identifier used as `JobPosting.Source`. Convention: `gcjobs`, `workday-cisco`, `workday-trendmicro`, `mitel`, etc. Lowercase, hyphenated. |
| `postings[]` | ✅ | Array. Empty is OK (means "we tried, found nothing"). |
| `postings[].title` | ✅ | Job title as the underlying source presents it. |
| `postings[].company` | ✅ | Hiring organization. For federal roles use the department (e.g. "Service Canada", "IRCC"). |
| `postings[].location` | ✅ | Free-text. **Include "Canada" / "Ottawa" / "Quebec" / etc. in the string** so the radar's existing `location_allow` filter matches. The filter has province names but not two-letter codes. |
| `postings[].url` | ✅ | Canonical link to the full posting. |
| `postings[].description` | ✅ | Full JD text. **Strip HTML before serialising** — the radar's scorer uses this verbatim as input. Newlines are fine; tags are not. |
| `postings[].ats_id` | optional but strongly recommended | Per-source unique id. Improves dedup and live-check. |
| `postings[].posted_at` | optional | ISO date or full timestamp. |
| `postings[].department` | optional | For ATS sources where it's separate from `company`. |
| `postings[].metadata` | optional | Free-form object for source-specific extras. The radar passes this through to the scorer. Examples: `classification: "IT-02"` for GC Jobs, `req_id: "12345"` for Workday, `close_date: "..."` for any time-bounded posting. |

**Stripping HTML in the description**: dispatch should prefer `innerText`/`textContent` over `innerHTML`. The radar's scorer doesn't render markup — tags become noise tokens that hurt match quality.

**Skip dead/closed postings**: if a posting has a `close_date` in the past at capture time, omit it from the output. Saves the radar's live-check pass.

**Empty `postings[]`** is acceptable and signals "tried, none matched the filter" — the radar handles it gracefully.

---

## Per-source capture instructions

### Tier 1 — GC Jobs (federal IT postings)

**Why this is the priority**: the user has 3 years of paid production experience on canada.ca search infrastructure (Service Canada, Principal Publisher / Search team — see `data/cv.md`). Federal IT-01 / IT-02 / IT-03 classifications are the band where the honest profile is most competitive, and the entire channel is currently invisible to the radar.

**URL**: https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page2440?fromMenu=true&toggleLanguage=en

**Steps**:

1. Navigate. Wait for the search panel + results table to render.
2. Apply filters in the UI:
   - Classification: IT-01, IT-02, IT-03 (multi-select if available, else iterate one at a time and union the results).
   - Status: Open positions only.
   - Language: keep all results — language requirement is a per-posting field; the radar's prompt rubric handles fluent-French gating.
3. Iterate all result pages. Federal portal uses traditional ASP.NET pagination; there's a "Next" button or page numbers at the bottom of the results table.
4. For each row:
   - Click into the posting (probably opens in same tab; capture URL first, navigate, capture description, navigate back).
   - Extract: `title`, `classification` (the "IT-XX" code), `department` (the hiring agency), `location` (city, province, country — make sure "Canada" appears in the string), `language_requirement` (BBB / CBC / etc.), `close_date`, `url` (canonical link to posting), `description` (full JD text).
5. Skip postings whose `close_date` is in the past.
6. Skip IT-04 and above (the user's eligibility profile lists those as out of band — too senior).
7. Output to `data/captured/gcjobs.json` with `source: "gcjobs"` and `captured_at` set to the dispatch run time.
8. `git add data/captured/gcjobs.json && git commit -m "chore(captured): gcjobs $(date -u +%Y-%m-%d)" && git push`.

**Acceptance**: a typical week-of-postings file should contain 5–30 IT-01/IT-02/IT-03 entries. After the first capture lands, run a manual `workflow_dispatch` of `daily-scan.yml` from the GitHub UI — the next digest should include federal postings in the eligible pool.

### Tier 2 — Workday-hosted enterprises

**Tenants and their search URLs** (best-guess; the shard / board id may need adjusting per tenant — probe and update as you go):

| Tenant | URL |
|---|---|
| Trend Micro | `https://trendmicro.wd1.myworkdayjobs.com/External` |
| Cisco | `https://cisco.wd5.myworkdayjobs.com/External` |
| Ciena | `https://ciena.wd5.myworkdayjobs.com/External_Careers` |
| BlackBerry | `https://blackberry.wd3.myworkdayjobs.com/BlackBerry` |
| Nokia | `https://nokia.wd3.myworkdayjobs.com/nokia` |
| Ericsson | `https://ericsson.wd1.myworkdayjobs.com/ericsson` |
| IBM Canada | `https://ibmglobal.wd1.myworkdayjobs.com/External` |
| Accenture | `https://accenture.wd103.myworkdayjobs.com/AccentureCareers` |

**Steps per tenant**:

1. Navigate to the URL. Wait for listings to render (Workday uses a list of cards, not a table).
2. Apply filters in the UI:
   - Country = Canada (or location set: Ottawa / Kanata / Waterloo / Ontario depending on the tenant).
   - Job Family / Category = Engineering or Software (where available — some tenants don't expose this filter).
3. Iterate all pages. Workday paginates with 20–50 per page; numeric pager at bottom or a "Load more" button.
4. For each card:
   - Click in to capture the full description.
   - Extract: `title`, `location`, `posted_at` (Workday usually shows a relative date — convert to absolute), `req_id` (Workday's internal posting ID, often visible in the URL like `.../job/.../R-12345`), `url` (canonical link), `description`.
5. Output one file per tenant to `data/captured/workday-<tenant>.json` with `source: "workday-<tenant>"` (e.g. `source: "workday-cisco"`). One file per tenant lets dispatch retry one without redoing all eight.
6. Commit + push. Reasonable to commit all eight files in a single commit.

**Robustness**: if any tenant's Workday is in a maintenance window (returns "service temporarily unavailable" or 5xx), log a warning and skip that tenant for this run — don't fail the whole capture. The previous tenants' files stay valid.

**Acceptance**: at least 3 Workday-tenant postings in the eligible pool of the next digest. Trend Micro typically has an Applied AI Backend Software Developer role open; if it's still listed, it should appear.

### Tier 3 — Jobboom + Magnet

Lower priority; the radar already covers Jobillico for Quebec and the existing aggregators for remote roles. Skip if Tiers 1 + 2 are full work.

If dispatch has spare cycles:
- **Jobboom**: `jobboom.com/en/jobs` — server-rendered HTML, but I couldn't figure out the search filter URL params. Open DevTools, perform a search for "software developer" in "Outaouais", inspect the resulting URL, capture the filter syntax. Once the URL pattern is known, this could potentially run via plain curl (no headless needed) — write the URL pattern back into this doc and I'll wire a static source.
- **Magnet** (`magnet.today`): unknown if still active. Quick probe; skip if defunct.

### Tier 4 — Ottawa shops on bespoke ATS

Lower priority. Each is a separate per-company capture.

| Company | URL | Notes |
|---|---|---|
| Mitel | `careers.mitel.com` | SuccessFactors-based; filter to Ottawa. |
| Kinaxis | `careers.kinaxis.com` | Their own ATS; filter to Ottawa. |
| Calian | `careers.calian.com` | Custom WordPress portal; honour `live_check_mode: require_ok` because at least one listing was 404 on a recent radar run. |
| Klipfolio | (Lever, currently in hiring freeze) | If the public openings list is empty, capture nothing — this becomes useful when they unfreeze. |
| You.i | (Greenhouse) | Should already be covered by the radar's Greenhouse source if we have the right board id; verify and if not, send back the correct slug. |

One file per company: `data/captured/mitel.json`, `data/captured/kinaxis.json`, etc.

### Tier 5 — Specialised boards

Lower priority. Optional.

- `dotnetjobs.com` — explicitly .NET-only, smaller volume but high signal.
- `wellfound.com/jobs` (formerly AngelList) — startup-focused; capture only the unauthenticated listings page.
- `workatastartup.com` — YC-backed startups. The API path I guessed returned 404; if dispatch can find the right endpoint from DevTools, capture from there.

---

## What I've already shipped on the radar side

- `src/JobRadar.Sources/CapturedJsonSource.cs` — reads every `data/captured/*.json` matching the schema above and yields `JobPosting`s into the pipeline. Tagged `LocationConfidence: Authoritative` because dispatch is presumed to scrape the underlying ATS's structured location field.
- `data/captured/.gitkeep` — directory reservation. Drop your JSON files alongside it.
- Tests covering: missing dir → empty result no error, malformed file → logged + skipped (no exception), valid file → postings emitted, stale `captured_at` → warning logged but postings still flow.
- Default `LiveCheckMode: BestEffort` for any source name starting with `captured`/`gcjobs`/`workday-` — so the live-check pass tries the URL but doesn't drop on 404 (the capture just ran; URLs should be live).

The first valid file dispatch commits will start showing up in the digest on the next cron. No additional radar-side work needed once the schema matches.

---

## Out of scope

- **LinkedIn**: explicit no. The user keeps LinkedIn alerts as a manual-review channel.
- **Indeed / Glassdoor**: gated behind anti-bot infra; deferred until a paid syndication API is in scope. See the comment in `config/sources.yml`.
- **The radar's existing scoring / cv.md / rubric**: dispatch shouldn't change any of these; they were the subject of last week's work and are dialled in.

---

## Quick verification checklist for dispatch

After committing a capture file:

1. `cat data/captured/<source>.json | jq '. | {captured_at, source, count: (.postings | length)}'` — confirms the file parses and shows the count.
2. `dotnet test --filter CapturedJsonSourceTests` — confirms the file matches the schema. A schema mismatch will surface here loudly.
3. `git push origin main` — fires nothing; just publishes the file. The next 12:00 UTC cron, or a manual `workflow_dispatch`, picks it up.
4. After the run, the digest email's `Run complete in ... Fetched: ... captured=N` line shows how many postings the captured-source adapter contributed.

Questions / schema clarifications: open an issue against this repo or message back through the user.
