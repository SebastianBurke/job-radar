# job-radar captures

Standalone local scraper for JS-rendered job sites the radar's curl-only
HTTP pipeline can't reach. Runs on a Mac under launchd, writes JSON
files into `data/captured/`, commits and pushes them. The next radar
cron picks them up automatically.

The radar side is unchanged: `src/JobRadar.Sources/CapturedJsonSource.cs`
reads every `data/captured/*.json` matching the schema and folds those
postings into the same dedup → live-check → score pipeline. Schema and
contract details live in `HEADLESS-SOURCE-HANDOFF.md` at the repo root.

## Prerequisites

- macOS (the launchd plist targets macOS; the Node CLI itself runs on
  any platform if you invoke it manually).
- Node 20 or newer (`node --version`).
- pnpm: `npm install -g pnpm` or `brew install pnpm`.
- Git configured to push to this repo (the writer runs `git push` after
  serializing the JSON files).

## Setup

```bash
cd tools/captures
pnpm install
pnpm exec playwright install chromium
```

Playwright downloads its own Chromium build into
`~/Library/Caches/ms-playwright/`. Your system Chrome is untouched.

## Running

Run a single source manually:

```bash
pnpm capture --source=gcjobs
```

Run multiple specific sources in one shot:

```bash
pnpm capture --source=gcjobs --source=workday-cisco
```

Run every registered source:

```bash
pnpm capture --all
```

Run without committing or pushing (useful for local iteration):

```bash
pnpm capture --source=gcjobs --no-push
```

Output lands at `data/captured/<source>.json` relative to the repo
root. Inspect:

```bash
jq '. | {captured_at, source, count: (.postings | length)}' \
  ../../data/captured/gcjobs.json
```

To verify the radar still parses the file correctly:

```bash
cd ../..
dotnet test --filter CapturedJsonSourceTests
```

## Adding a source

1. Create `src/sources/<name>.ts` exporting a `SourceModule`:

   ```typescript
   import type { SourceModule } from '../common/types.js';

   export const exampleSource: SourceModule = {
     name: 'example',
     async capture(ctx) {
       // Use ctx.browser (Playwright) and ctx.logger.
       // Return one of:
       //   { kind: 'ok', file, listingFingerprint }
       //   { kind: 'skipped', reason }            // skip-if-stale
       //   { kind: 'bailout', reason }            // bot-challenge / maintenance
       return { kind: 'ok', file: { captured_at: new Date().toISOString(), source: 'example', postings: [] } };
     },
   };
   ```

2. Register it in `src/sources/index.ts`:

   ```typescript
   import { exampleSource } from './example.js';
   export const SOURCES = [exampleSource];
   ```

3. Add a fixture-based unit test next to the source
   (`<name>.test.ts`) that asserts the parser produces a valid
   posting from saved HTML. Fixtures live under
   `<name>.fixtures/`.

4. Run `pnpm test` and `pnpm typecheck` to verify.

## Schedule install (launchd)

Edit `install/com.sebastianburke.jobradar-captures.plist` and replace
every `REPLACE_REPO_PATH` with the absolute path to your local
job-radar checkout (for example `/Users/sebastian/code/job-radar`).

Then install:

```bash
cp install/com.sebastianburke.jobradar-captures.plist \
   ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.sebastianburke.jobradar-captures.plist
```

The job runs at 5, 9, 13, 17, 21 local time. The 5 AM run finishes
well before the radar's 12:00 UTC cron, so fresh captures are always
on `main` when the cron fires. The remaining slots keep things fresh
through the business day.

To inspect log output:

```bash
tail -f tools/captures/.seen/launchd.out.log
tail -f tools/captures/.seen/launchd.err.log
```

To stop the schedule:

```bash
launchctl unload ~/Library/LaunchAgents/com.sebastianburke.jobradar-captures.plist
```

## Skip-if-stale

After a successful run a source's listing-page fingerprint (sha256 of
the served HTML) lands in `tools/captures/.seen/<source>.json`. On
the next run, if the fingerprint matches AND the previous run was
within 6 hours, the source returns `skipped` early. This keeps
requests off the target sites without sacrificing freshness — the
4-hour schedule grid lets the gate flip on the next slot.

`.seen/` is local-only (gitignored). Each machine running this
scraper builds its own state.

## Bot-detection bailout

If a target site returns a Cloudflare challenge or a "service
temporarily unavailable" maintenance page, the source returns
`{ kind: 'bailout', reason }`. The runner writes an otherwise-empty
captures file with `metadata.bailout_reason` set so the radar logs
zero postings for that source on that run, but the rest of the
pipeline continues. The next scheduled fire retries; nothing else
needs to be done.

## Troubleshooting

**`pnpm: command not found` when launchd fires.** Add the directory
containing your `pnpm` binary (check with `which pnpm`) to the plist's
`EnvironmentVariables.PATH`. Defaults cover both Apple Silicon
(`/opt/homebrew/bin`) and Intel Homebrew (`/usr/local/bin`). Reload
after editing:

```bash
launchctl unload ~/Library/LaunchAgents/com.sebastianburke.jobradar-captures.plist
launchctl load   ~/Library/LaunchAgents/com.sebastianburke.jobradar-captures.plist
```

**`Browser was not found` errors.** Run
`pnpm exec playwright install chromium` once. Re-run after Playwright
upgrades.

**`git push` fails.** The script uses your existing git credentials.
If the local push works (`git push origin main` from the repo root)
but the launchd push doesn't, the agent likely lacks the right
keychain. Adding the SSH key to the macOS keychain
(`ssh-add --apple-use-keychain ~/.ssh/id_ed25519`) usually fixes it.

**Empty `postings: []` with `bailout_reason` set.** A site is in
maintenance or returning a bot challenge. No action needed; the next
scheduled run retries. If it persists for >24 hours for one source,
the selector probably changed — open the site in a real browser and
update the source's parser.

**Captures keep getting skipped.** Skip-if-stale is gating you. To
force a run, delete the seen file:

```bash
rm tools/captures/.seen/<source>.json
pnpm capture --source=<source> --no-push
```

**Runs take longer than 30 minutes.** Either the target site got
slower or a source is misbehaving. Run with `JOBRADAR_LOG_LEVEL=debug`
to see per-step timings.

## Layout

```
tools/captures/
  README.md                        # this file
  package.json
  tsconfig.json
  vitest.config.ts
  install/
    com.sebastianburke.jobradar-captures.plist
  config/
    (workday-tenants.yml etc., land in later tiers)
  src/
    capture.ts                     # CLI entrypoint
    common/
      schema.ts                    # CapturedFile / CapturedPosting types
      browser.ts                   # Playwright launch + bot-challenge detector
      writer.ts                    # JSON write + git commit/push
      retry.ts                     # exponential-backoff helper
      stale.ts                     # listing fingerprint + seen.json
      logger.ts                    # leveled console logger
      types.ts                     # SourceModule / CaptureContext / CaptureResult
    sources/
      index.ts                     # registry — append each source here
      <name>.ts                    # one file per source
      <name>.test.ts               # contract test from saved HTML
      <name>.fixtures/             # HTML fixtures used by the test
  .seen/                           # local-only state (gitignored)
```

## Why not run this in GitHub Actions?

Putting Playwright inside the radar's GitHub Actions runner is a
non-starter — Playwright in CI is heavy (5+ minutes added to every
cron), brittle (CI runners don't share fingerprints with humans), and
fights anti-bot heuristics that already trip on cloud IPs. Running on
the candidate's Mac is the right call: the Mac is always on, has
unmetered residential bandwidth, runs the schedule with launchd, and
the resulting JSON files are the only thing that needs to leave the
machine. The git push is the API boundary.
