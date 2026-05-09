import type { Browser } from 'playwright';
import type { CapturedFile } from './schema.js';
import type { Logger } from './logger.js';

export interface StalenessGate {
  shouldSkip(fingerprint: string): boolean;
}

export interface CaptureContext {
  browser: Browser;
  logger: Logger;
  sourceName: string;
  staleness: StalenessGate;
}

export type CaptureResult =
  | { kind: 'ok'; file: CapturedFile; listingFingerprint?: string }
  | { kind: 'skipped'; reason: string }
  | { kind: 'bailout'; reason: string; partial?: CapturedFile };

export interface SourceModule {
  name: string;
  capture(ctx: CaptureContext): Promise<CaptureResult>;
}
