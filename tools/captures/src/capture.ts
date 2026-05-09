import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { launchBrowser } from './common/browser.js';
import { createLogger } from './common/logger.js';
import { sleep } from './common/retry.js';
import { readSeen, shouldSkipForStaleness, writeSeen } from './common/stale.js';
import { commitAndPush, writeCaptureFile } from './common/writer.js';
import type { CaptureContext, CaptureResult, SourceModule } from './common/types.js';
import { SOURCES } from './sources/index.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = join(__dirname, '..', '..', '..');
const CAPTURED_DIR = join(REPO_ROOT, 'data', 'captured');
const SEEN_DIR = join(REPO_ROOT, 'tools', 'captures', '.seen');

async function main(): Promise<void> {
  const { values } = parseArgs({
    options: {
      source: { type: 'string', multiple: true },
      all: { type: 'boolean', default: false },
      'no-push': { type: 'boolean', default: false },
    },
    allowPositionals: false,
  });

  const logger = createLogger('captures');
  const sourceArgs = values.source ?? [];
  const selected = resolveSelected(values.all === true, sourceArgs, logger);
  if (selected.length === 0) {
    logger.warn('No sources to run.');
    return;
  }

  const browser = await launchBrowser();
  const writtenSources: string[] = [];
  try {
    for (const source of selected) {
      const sLogger = logger.child(source.name);
      const seenPath = join(SEEN_DIR, `${source.name}.json`);
      const previous = await readSeen(seenPath);

      const ctx: CaptureContext = {
        browser,
        logger: sLogger,
        sourceName: source.name,
        staleness: {
          shouldSkip: (fp) => shouldSkipForStaleness(previous, fp),
        },
      };

      let result: CaptureResult;
      try {
        result = await source.capture(ctx);
      } catch (err) {
        sLogger.error(`Capture failed: ${err instanceof Error ? err.message : String(err)}`);
        continue;
      }

      if (result.kind === 'ok') {
        await writeCaptureFile(CAPTURED_DIR, result.file, sLogger);
        if (result.listingFingerprint) {
          await writeSeen(seenPath, {
            last_run_at: new Date().toISOString(),
            listing_fingerprint: result.listingFingerprint,
          });
        }
        writtenSources.push(source.name);
      } else if (result.kind === 'skipped') {
        sLogger.info(`Skipped: ${result.reason}`);
      } else {
        const file = result.partial ?? {
          captured_at: new Date().toISOString(),
          source: source.name,
          postings: [],
          metadata: { bailout_reason: result.reason },
        };
        await writeCaptureFile(CAPTURED_DIR, file, sLogger);
        writtenSources.push(source.name);
        sLogger.warn(`Bailout: ${result.reason}`);
      }

      // Pause briefly between sources to share the host budget evenly.
      await sleep(1000);
    }
  } finally {
    await browser.close();
  }

  if (values['no-push']) {
    logger.info('--no-push set; skipping commit + push.');
  } else {
    await commitAndPush(REPO_ROOT, CAPTURED_DIR, writtenSources, logger);
  }
}

function resolveSelected(
  all: boolean,
  requested: string[],
  logger: ReturnType<typeof createLogger>,
): SourceModule[] {
  if (all) return [...SOURCES];
  if (requested.length === 0) {
    logger.error('Specify --source=<name> (one or more) or --all.');
    process.exit(2);
  }
  const wanted = new Set(requested);
  const selected = SOURCES.filter((s) => wanted.has(s.name));
  const missing = [...wanted].filter((n) => !selected.some((s) => s.name === n));
  if (missing.length) {
    const known = SOURCES.map((s) => s.name).join(', ') || '(none registered)';
    logger.error(`Unknown source(s): ${missing.join(', ')}. Registered: ${known}`);
    process.exit(2);
  }
  return selected;
}

main().catch((err: unknown) => {
  console.error(err);
  process.exit(1);
});
