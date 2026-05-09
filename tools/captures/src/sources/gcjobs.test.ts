import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  buildPostingFromBlock,
  composeDescription,
  isTechTitle,
  normalizeCanadianLocation,
  parseClosingDate,
  parseListingPage,
  parseTotalPages,
  stripJsessionid,
  type ListingBlock,
} from './gcjobs.js';

const FIXTURES_DIR = join(dirname(fileURLToPath(import.meta.url)), 'gcjobs.fixtures');

function fixture(name: string): string {
  return readFileSync(join(FIXTURES_DIR, name), 'utf8');
}

describe('parseTotalPages', () => {
  it('reads "of N" from the pagination footer', () => {
    expect(parseTotalPages(fixture('listing.html'))).toBe(3);
  });

  it('falls back to the highest requestedPage when "of N" is missing', () => {
    const html = '<a href="page2440?requestedPage=7">7</a><a href="page2440?requestedPage=12">12</a>';
    expect(parseTotalPages(html)).toBe(12);
  });

  it('returns undefined when no pagination markers are found', () => {
    expect(parseTotalPages('<p>nothing here</p>')).toBeUndefined();
  });
});

describe('parseListingPage', () => {
  const blocks = parseListingPage(fixture('listing.html'));

  it('extracts every <li> with a page1800 link', () => {
    expect(blocks).toHaveLength(4);
  });

  it('captures titles correctly', () => {
    expect(blocks.map((b) => b.title)).toEqual([
      'Senior Digital Product Developer',
      'Light and Heavy Vehicle Drivers',
      'Software Engineer (Closed)',
      'Data Analyst',
    ]);
  });

  it('absolutizes URLs against the GC Jobs origin', () => {
    expect(blocks[0]!.url).toMatch(/^https:\/\/emploisfp-psjobs\.cfp-psc\.gc\.ca\//);
  });

  it('strips ;jsessionid=... from URLs', () => {
    const dataAnalyst = blocks.find((b) => b.title === 'Data Analyst');
    expect(dataAnalyst).toBeDefined();
    expect(dataAnalyst!.url).not.toMatch(/jsessionid/i);
    expect(dataAnalyst!.url).toContain('poster=3333333');
  });

  it('extracts the poster id', () => {
    expect(blocks[0]!.posterId).toBe('2422931');
  });

  it('extracts closing date, organization, location, language and salary', () => {
    const dev = blocks[0]!;
    expect(dev.closingDate).toBe('2026-05-10');
    expect(dev.organization).toBe('House of Commons (Employees)');
    expect(dev.location).toBe('Ottawa (Ontario)');
    expect(dev.language).toBe('Bilingual - imperative');
    expect(dev.salary).toBe('$98,093 to $124,115');
  });
});

describe('isTechTitle', () => {
  it('keeps tech-relevant titles', () => {
    expect(isTechTitle('Senior Digital Product Developer')).toBe(true);
    expect(isTechTitle('Software Engineer')).toBe(true);
    expect(isTechTitle('Data Analyst')).toBe(true);
    expect(isTechTitle('Web Developer')).toBe(true);
    expect(isTechTitle('Cloud Architect')).toBe(true);
    expect(isTechTitle('DevOps Specialist')).toBe(true);
    expect(isTechTitle('Information Technology Advisor')).toBe(true);
  });

  it('rejects non-tech titles', () => {
    expect(isTechTitle('Light and Heavy Vehicle Drivers')).toBe(false);
    expect(isTechTitle('Maintenance Worker III')).toBe(false);
    expect(isTechTitle('Investment Funds Advisor')).toBe(false);
    expect(isTechTitle('Court Administrative Assistant')).toBe(false);
    expect(isTechTitle('Calling all Tradespeople in Victoria!')).toBe(false);
  });

  it('is case-insensitive', () => {
    expect(isTechTitle('SOFTWARE DEVELOPER')).toBe(true);
    expect(isTechTitle('software developer')).toBe(true);
  });
});

describe('buildPostingFromBlock + composeDescription', () => {
  const block: ListingBlock = {
    title: 'Senior Digital Product Developer',
    url: 'https://emploisfp-psjobs.cfp-psc.gc.ca/psrs-srfp/applicant/page1800?poster=2422931',
    posterId: '2422931',
    closingDate: '2026-05-10',
    organization: 'House of Commons (Employees)',
    location: 'Ottawa (Ontario)',
    language: 'Bilingual - imperative',
    salary: '$98,093 to $124,115',
  };

  it('produces a posting matching the captured-file schema', () => {
    const p = buildPostingFromBlock(block);
    expect(p.title).toBe('Senior Digital Product Developer');
    expect(p.company).toBe('House of Commons (Employees)');
    expect(p.department).toBe('House of Commons (Employees)');
    expect(p.url).toBe(block.url);
    expect(p.ats_id).toBe('2422931');
    expect(p.location).toContain('Ottawa');
    expect(p.location).toContain('Canada');
    expect(p.description).toContain('Senior Digital Product Developer');
    expect(p.description).toContain('Bilingual - imperative');
    expect(p.description).toContain('$98,093 to $124,115');
  });

  it('exposes structured metadata', () => {
    const p = buildPostingFromBlock(block);
    const m = p.metadata ?? {};
    expect(m.external).toBe(true);
    expect(m.close_date).toBe('2026-05-10');
    expect(m.language_requirement).toBe('Bilingual - imperative');
    expect(m.salary).toBe('$98,093 to $124,115');
    expect(m.poster_id).toBe('2422931');
  });

  it('falls back to "Government of Canada (partner)" when organization is absent', () => {
    const p = buildPostingFromBlock({ ...block, organization: undefined });
    expect(p.company).toBe('Government of Canada (partner)');
  });

  it('keeps the raw close-date string when unparseable', () => {
    const p = buildPostingFromBlock({ ...block, closingDate: 'soon' });
    const m = p.metadata ?? {};
    expect(m.close_date).toBeUndefined();
    expect(m.close_date_raw).toBe('soon');
  });

  it('description includes all populated fields', () => {
    const desc = composeDescription(block);
    expect(desc).toMatch(/Position: Senior Digital Product Developer/);
    expect(desc).toMatch(/Organization: House of Commons/);
    expect(desc).toMatch(/Location: Ottawa/);
    expect(desc).toMatch(/Language requirement: Bilingual/);
    expect(desc).toMatch(/Salary: \$98,093/);
    expect(desc).toMatch(/Closing date: 2026-05-10/);
    expect(desc).toMatch(/external partner posting/i);
  });
});

describe('parseClosingDate', () => {
  it('parses ISO yyyy-mm-dd', () => {
    expect(parseClosingDate('2026-05-10')?.toISOString().slice(0, 10)).toBe('2026-05-10');
  });

  it('parses "22 May 2026"', () => {
    expect(parseClosingDate('22 May 2026 - 23:59, Pacific Time')?.toISOString().slice(0, 10)).toBe('2026-05-22');
  });

  it('parses "May 22, 2026"', () => {
    expect(parseClosingDate('May 22, 2026')?.toISOString().slice(0, 10)).toBe('2026-05-22');
  });

  it('returns undefined for unrecognised input', () => {
    expect(parseClosingDate('soon')).toBeUndefined();
  });
});

describe('normalizeCanadianLocation', () => {
  it('appends Canada when missing', () => {
    expect(normalizeCanadianLocation('Ottawa (Ontario)')).toBe('Ottawa (Ontario), Canada');
  });

  it('leaves existing Canada mention alone', () => {
    expect(normalizeCanadianLocation('Ottawa, Ontario, Canada')).toBe('Ottawa, Ontario, Canada');
  });

  it('handles a trailing comma', () => {
    expect(normalizeCanadianLocation('Ottawa (Ontario),')).toBe('Ottawa (Ontario), Canada');
  });

  it('falls back to "(unspecified)" when blank', () => {
    expect(normalizeCanadianLocation('   ')).toBe('(unspecified)');
  });
});

describe('stripJsessionid', () => {
  it('removes ;jsessionid=... before the query string', () => {
    const url = 'https://x.example/path;jsessionid=ABC?foo=bar';
    expect(stripJsessionid(url)).toBe('https://x.example/path?foo=bar');
  });

  it('is a no-op when no jsessionid is present', () => {
    const url = 'https://x.example/path?foo=bar';
    expect(stripJsessionid(url)).toBe(url);
  });
});
