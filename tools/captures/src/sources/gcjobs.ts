import * as cheerio from 'cheerio';
import type { BrowserContext, Page } from 'playwright';
import { detectBotChallenge, newContext } from '../common/browser.js';
import { sleep } from '../common/retry.js';
import { fingerprint } from '../common/stale.js';
import type { CapturedFile, CapturedPosting } from '../common/schema.js';
import type { CaptureContext, CaptureResult, SourceModule } from '../common/types.js';

// Federal portal entry point. The unauthenticated listing here surfaces
// external-partner postings (Crown corps, House of Commons, etc.) that route
// through a "you will leave the GC Jobs Web site" disclaimer and then to the
// partner's own ATS. Federal-internal classified postings (IT-01/02/03 etc.)
// require login and aren't reachable here — see scratch notes during the
// 2026-05-09 implementation pass.
export const SEARCH_URL =
  'https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page2440?fromMenu=true&toggleLanguage=en';

const PAGINATION_BASE =
  'https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page2440';

// Title-keyword filter that approximates "tech-relevant role". The radar's
// scorer further refines based on the candidate's CV; this gate just keeps the
// captured-postings volume in a sane range. Inclusive on purpose: missing a
// real opportunity is more costly than a false positive the scorer trims.
//
// Two pools: substring keywords (long enough to be unambiguous) and short
// keywords matched on word boundaries (so e.g. "CTO" matches "CTO Office" but
// not "Victoria").
export const TECH_TITLE_KEYWORDS: readonly string[] = [
  'software',
  'developer',
  'programmer',
  'engineer',
  'engineering',
  'devops',
  'devsecops',
  'platform',
  'cloud',
  'technical',
  'technology',
  'architect',
  'systems',
  'system administrator',
  'sysadmin',
  'database',
  'data scientist',
  'data analyst',
  'data engineer',
  'data architect',
  'full stack',
  'full-stack',
  'frontend',
  'front-end',
  'front end',
  'backend',
  'back-end',
  'back end',
  'dotnet',
  'typescript',
  'javascript',
  'python',
  'infrastructure',
  'digital product',
  'digital developer',
  'informatics',
  'informatique',
  'automation',
  'quality assurance',
  'application',
  'computer',
  'network engineer',
  'machine learning',
  'product manager',
  'product owner',
  'tech lead',
  'technical lead',
  'analyst',
  'specialist',
  'science officer',
  'scientific',
];

const TECH_SHORT_KEYWORDS_RE = /\b(web|qa|sre|ml|\.net)\b/i;

const PER_NAVIGATION_PAUSE_MS = 1500;
const NAVIGATION_TIMEOUT_MS = 45000;

export interface ListingBlock {
  title: string;
  url: string;
  posterId?: string;
  closingDate?: string;
  organization?: string;
  location?: string;
  language?: string;
  salary?: string;
}

export const gcjobsSource: SourceModule = {
  name: 'gcjobs',
  capture,
};

async function capture(ctx: CaptureContext): Promise<CaptureResult> {
  const browserCtx = await newContext(ctx.browser);
  try {
    return await captureWithContext(browserCtx, ctx);
  } finally {
    await browserCtx.close();
  }
}

async function captureWithContext(
  browserCtx: BrowserContext,
  ctx: CaptureContext,
): Promise<CaptureResult> {
  const page = await browserCtx.newPage();
  page.setDefaultTimeout(NAVIGATION_TIMEOUT_MS);

  ctx.logger.info('Loading first listing page...');
  try {
    await page.goto(SEARCH_URL, { waitUntil: 'networkidle' });
  } catch (err) {
    ctx.logger.warn(`Initial navigation failed: ${formatErr(err)}`);
    return { kind: 'bailout', reason: `initial-navigation-failed: ${formatErr(err)}` };
  }

  const challenge = await detectBotChallenge(page);
  if (challenge) {
    ctx.logger.warn(`Bot challenge: ${challenge}`);
    return { kind: 'bailout', reason: challenge };
  }

  const firstHtml = await page.content();
  const totalPages = parseTotalPages(firstHtml) ?? 1;
  ctx.logger.info(`Listing has ${totalPages} pages.`);

  const allBlocks: ListingBlock[] = [];
  allBlocks.push(...parseListingPage(firstHtml));

  for (let pageNum = 2; pageNum <= totalPages; pageNum++) {
    await sleep(PER_NAVIGATION_PAUSE_MS);
    const url = `${PAGINATION_BASE}?requestedPage=${pageNum}&fromPage=1&tab=1&log=false`;
    try {
      await page.goto(url, { waitUntil: 'networkidle' });
    } catch (err) {
      ctx.logger.warn(`Failed to load listing page ${pageNum}: ${formatErr(err)}`);
      continue;
    }
    const innerChallenge = await detectBotChallenge(page);
    if (innerChallenge) {
      ctx.logger.warn(`Bot challenge on page ${pageNum}: ${innerChallenge}`);
      return { kind: 'bailout', reason: innerChallenge };
    }
    const html = await page.content();
    const blocks = parseListingPage(html);
    ctx.logger.info(`Page ${pageNum}: ${blocks.length} blocks (running total ${allBlocks.length + blocks.length}).`);
    allBlocks.push(...blocks);
  }

  if (allBlocks.length === 0) {
    ctx.logger.warn('No listing blocks parsed; emitting empty file.');
    return emptyOk();
  }

  // Dedupe by URL — paginated listings can occasionally repeat across pages
  // when new postings arrive between navigations.
  const dedupedByUrl = new Map<string, ListingBlock>();
  for (const block of allBlocks) {
    if (!dedupedByUrl.has(block.url)) dedupedByUrl.set(block.url, block);
  }
  const allBlocksUnique = [...dedupedByUrl.values()];

  const fp = fingerprint([...dedupedByUrl.keys()].sort().join('\n'));
  if (ctx.staleness.shouldSkip(fp)) {
    return { kind: 'skipped', reason: 'listing fingerprint unchanged within staleness window' };
  }

  const today = new Date();
  today.setUTCHours(0, 0, 0, 0);
  const postings: CapturedPosting[] = [];
  let droppedNonTech = 0;
  let droppedClosed = 0;
  for (const block of allBlocksUnique) {
    if (!isTechTitle(block.title)) {
      droppedNonTech++;
      continue;
    }
    if (block.closingDate) {
      const close = parseClosingDate(block.closingDate);
      if (close && close < today) {
        droppedClosed++;
        continue;
      }
    }
    postings.push(buildPostingFromBlock(block));
  }

  ctx.logger.info(
    `Kept ${postings.length} tech postings (filtered out ${droppedNonTech} non-tech, ${droppedClosed} closed) from ${allBlocksUnique.length} unique listing blocks.`,
  );

  const file: CapturedFile = {
    captured_at: new Date().toISOString(),
    source: 'gcjobs',
    postings,
  };
  return { kind: 'ok', file, listingFingerprint: fp };
}

function emptyOk(): CaptureResult {
  return {
    kind: 'ok',
    file: {
      captured_at: new Date().toISOString(),
      source: 'gcjobs',
      postings: [],
    },
  };
}

export function parseTotalPages(html: string): number | undefined {
  // Pagination footer renders as e.g.: "Page 1, 2, 3, ... 19 of 19 [Next / Last]"
  const m = html.match(/of\s+(\d+)\s*\[\s*(?:<a[^>]*>\s*)?Next/i);
  if (m) return parseInt(m[1]!, 10);
  // Fallback: pick the largest requestedPage=N seen on the page.
  const nums = [...html.matchAll(/requestedPage=(\d+)/g)]
    .map((mm) => parseInt(mm[1]!, 10))
    .filter((n) => Number.isFinite(n));
  if (nums.length) return Math.max(...nums);
  return undefined;
}

export function parseListingPage(html: string): ListingBlock[] {
  const $ = cheerio.load(html);
  const blocks: ListingBlock[] = [];

  // Each posting is an <li> inside the search results <ol> (typically
  // class="posterInfo list-more-space"). Be permissive: accept any <li>
  // that contains a page1800 link, in case the class structure shifts.
  $('li').each((_, li) => {
    const link = $(li).find('a[href*="page1800"][href*="poster="]').first();
    const href = link.attr('href');
    if (!href) return;
    const title = normalizeWhitespace(link.text());
    if (!title) return;

    const url = stripJsessionid(absolutize(href, SEARCH_URL));
    const posterId = url.match(/poster=(\d+)/)?.[1];
    const cells = $(li).find('div.tableCell, td.tableCell');

    const leftRaw = cells.eq(0).text();
    const rightRaw = cells.eq(1).text();

    const closingDate = leftRaw.match(/Closing date:\s*([^\n\r]+)/i)?.[1]?.trim();

    // Organization / location are plain lines after "Closing date" in the left
    // cell. Take them in order, skipping empties and the closing-date line.
    const leftLines = leftRaw.split('\n').map((l) => l.trim()).filter(Boolean);
    let organization: string | undefined;
    let location: string | undefined;
    for (const line of leftLines) {
      if (/closing date/i.test(line)) continue;
      if (!organization) {
        organization = line;
      } else if (!location) {
        location = line;
        break;
      }
    }

    const salaryMatch = rightRaw.match(/\$[\d,]+(?:\s*(?:to|-|–)\s*\$[\d,]+)?/);
    const salary = salaryMatch?.[0]?.trim();
    const language = normalizeWhitespace(
      rightRaw.replace(/\$[\d,]+(?:\s*(?:to|-|–)\s*\$[\d,]+)?/g, ''),
    );

    blocks.push({
      title,
      url,
      posterId,
      closingDate,
      organization,
      location,
      language: language || undefined,
      salary: salary || undefined,
    });
  });

  return blocks;
}

export function buildPostingFromBlock(block: ListingBlock): CapturedPosting {
  const closeIso = block.closingDate ? toIsoDate(parseClosingDate(block.closingDate)) : undefined;
  const company = (block.organization?.trim() || 'Government of Canada (partner)').trim();

  const metadata: Record<string, unknown> = { external: true };
  if (closeIso) metadata.close_date = closeIso;
  else if (block.closingDate) metadata.close_date_raw = block.closingDate;
  if (block.language) metadata.language_requirement = block.language;
  if (block.salary) metadata.salary = block.salary;
  if (block.posterId) metadata.poster_id = block.posterId;

  return {
    ats_id: block.posterId,
    title: block.title,
    company,
    location: normalizeCanadianLocation(block.location ?? '(unspecified)'),
    url: block.url,
    description: composeDescription(block),
    department: block.organization?.trim(),
    metadata,
  };
}

export function composeDescription(block: ListingBlock): string {
  const lines: string[] = [`Position: ${block.title}`];
  if (block.organization) lines.push(`Organization: ${block.organization}`);
  if (block.location) lines.push(`Location: ${block.location}`);
  if (block.language) lines.push(`Language requirement: ${block.language}`);
  if (block.salary) lines.push(`Salary: ${block.salary}`);
  if (block.closingDate) lines.push(`Closing date: ${block.closingDate}`);
  lines.push(
    'Listed via the GC Jobs portal as an external partner posting. The full job description is hosted on the partner organization\'s site — follow the URL.',
  );
  return lines.join('\n');
}

export function isTechTitle(title: string): boolean {
  const lower = title.toLowerCase();
  if (TECH_TITLE_KEYWORDS.some((keyword) => lower.includes(keyword))) return true;
  return TECH_SHORT_KEYWORDS_RE.test(title);
}

export function parseClosingDate(s: string): Date | undefined {
  const trimmed = s.trim();
  // ISO yyyy-mm-dd
  let m = trimmed.match(/(\d{4})-(\d{1,2})-(\d{1,2})/);
  if (m) return new Date(Date.UTC(+m[1]!, +m[2]! - 1, +m[3]!));
  // "22 May 2026"
  m = trimmed.match(/(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})/);
  if (m) {
    const idx = monthIndex(m[2]!);
    if (idx !== undefined) return new Date(Date.UTC(+m[3]!, idx, +m[1]!));
  }
  // "May 22, 2026"
  m = trimmed.match(/([A-Za-z]+)\s+(\d{1,2}),?\s+(\d{4})/);
  if (m) {
    const idx = monthIndex(m[1]!);
    if (idx !== undefined) return new Date(Date.UTC(+m[3]!, idx, +m[2]!));
  }
  return undefined;
}

const MONTH_PREFIXES = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];

function monthIndex(name: string): number | undefined {
  const idx = MONTH_PREFIXES.indexOf(name.toLowerCase().slice(0, 3));
  return idx >= 0 ? idx : undefined;
}

function toIsoDate(d: Date | undefined): string | undefined {
  if (!d || Number.isNaN(d.getTime())) return undefined;
  return d.toISOString().slice(0, 10);
}

export function normalizeCanadianLocation(loc: string): string {
  const trimmed = loc.trim();
  if (!trimmed) return '(unspecified)';
  if (/canada/i.test(trimmed)) return trimmed;
  return /[,]\s*$/.test(trimmed) ? `${trimmed} Canada` : `${trimmed}, Canada`;
}

export function stripJsessionid(url: string): string {
  return url.replace(/;jsessionid=[^?#]*/i, '');
}

function normalizeWhitespace(s: string): string {
  return s.replace(/\s+/g, ' ').trim();
}

function absolutize(href: string, base: string): string {
  try {
    return new URL(href, base).toString();
  } catch {
    return href;
  }
}

function formatErr(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
