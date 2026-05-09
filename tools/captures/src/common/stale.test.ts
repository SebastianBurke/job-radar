import { describe, expect, it } from 'vitest';
import { fingerprint, shouldSkipForStaleness, STALE_THRESHOLD_HOURS } from './stale.js';

describe('fingerprint', () => {
  it('is deterministic for identical input', () => {
    expect(fingerprint('hello')).toBe(fingerprint('hello'));
  });

  it('differs for different input', () => {
    expect(fingerprint('hello')).not.toBe(fingerprint('world'));
  });
});

describe('shouldSkipForStaleness', () => {
  const fp = 'abc123';
  const now = new Date('2026-05-09T12:00:00Z');

  it('returns false when there is no prior record', () => {
    expect(shouldSkipForStaleness(null, fp, now)).toBe(false);
  });

  it('returns false when the fingerprint differs', () => {
    expect(
      shouldSkipForStaleness(
        { last_run_at: '2026-05-09T11:00:00Z', listing_fingerprint: 'different' },
        fp,
        now,
      ),
    ).toBe(false);
  });

  it('returns true when fingerprint matches and the last run is within the window', () => {
    expect(
      shouldSkipForStaleness(
        { last_run_at: '2026-05-09T11:00:00Z', listing_fingerprint: fp },
        fp,
        now,
      ),
    ).toBe(true);
  });

  it('returns false when the last run is past the staleness threshold', () => {
    const beyond = new Date(now.getTime() - (STALE_THRESHOLD_HOURS + 1) * 3_600_000).toISOString();
    expect(
      shouldSkipForStaleness({ last_run_at: beyond, listing_fingerprint: fp }, fp, now),
    ).toBe(false);
  });

  it('returns false when last_run_at is unparseable', () => {
    expect(
      shouldSkipForStaleness({ last_run_at: 'not-a-date', listing_fingerprint: fp }, fp, now),
    ).toBe(false);
  });
});
