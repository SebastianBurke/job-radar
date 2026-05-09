import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

export const STALE_THRESHOLD_HOURS = 6;

export interface SeenRecord {
  last_run_at: string;
  listing_fingerprint: string;
}

export function fingerprint(content: string): string {
  return createHash('sha256').update(content).digest('hex');
}

export async function readSeen(path: string): Promise<SeenRecord | null> {
  try {
    const raw = await readFile(path, 'utf8');
    const parsed = JSON.parse(raw) as Partial<SeenRecord>;
    if (typeof parsed.last_run_at === 'string' && typeof parsed.listing_fingerprint === 'string') {
      return { last_run_at: parsed.last_run_at, listing_fingerprint: parsed.listing_fingerprint };
    }
    return null;
  } catch {
    return null;
  }
}

export async function writeSeen(path: string, record: SeenRecord): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, JSON.stringify(record, null, 2) + '\n', 'utf8');
}

export function shouldSkipForStaleness(
  seen: SeenRecord | null,
  currentFingerprint: string,
  now: Date = new Date(),
): boolean {
  if (!seen) return false;
  if (seen.listing_fingerprint !== currentFingerprint) return false;
  const lastRun = Date.parse(seen.last_run_at);
  if (Number.isNaN(lastRun)) return false;
  const ageHours = (now.getTime() - lastRun) / 3_600_000;
  return ageHours >= 0 && ageHours < STALE_THRESHOLD_HOURS;
}
