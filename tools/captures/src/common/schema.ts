// Wire format consumed by src/JobRadar.Sources/CapturedJsonSource.cs.
// Keep in sync with the schema documented in HEADLESS-SOURCE-HANDOFF.md.

export interface CapturedFile {
  captured_at: string;
  source: string;
  postings: CapturedPosting[];
  metadata?: CapturedFileMetadata;
}

export interface CapturedFileMetadata {
  bailout_reason?: string;
  [key: string]: unknown;
}

export interface CapturedPosting {
  ats_id?: string;
  title: string;
  company: string;
  location: string;
  url: string;
  description: string;
  posted_at?: string;
  department?: string;
  metadata?: Record<string, unknown>;
}
