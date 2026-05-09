import { chromium, type Browser, type BrowserContext, type Page } from 'playwright';

// Latest Chrome on macOS — same UA the radar's first-party sources advertise
// on their HTTP requests, kept in sync with `User-Agent: JobRadar/1.0` style
// honesty by identifying as a real browser (which it is, here).
export const REALISTIC_UA =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';

export async function launchBrowser(): Promise<Browser> {
  return chromium.launch({ headless: true });
}

export async function newContext(browser: Browser): Promise<BrowserContext> {
  return browser.newContext({
    userAgent: REALISTIC_UA,
    viewport: { width: 1280, height: 800 },
    locale: 'en-CA',
    timezoneId: 'America/Toronto',
  });
}

// Bot-detection bailout: many anti-bot pages render the same Cloudflare /
// "service temporarily unavailable" markers. Sources can call this on the
// post-navigation page to decide whether to bail.
export async function detectBotChallenge(page: Page): Promise<string | null> {
  const url = page.url();
  const title = (await page.title()).toLowerCase();
  if (title.includes('just a moment') || title.includes('attention required')) {
    return `cloudflare-challenge:${title}`;
  }
  if (title.includes('access denied') || title.includes('forbidden')) {
    return `access-denied:${title}`;
  }
  const body = (await page.content()).toLowerCase();
  if (body.includes('cf-error-details') || body.includes('cf-browser-verification')) {
    return 'cloudflare-challenge:body-marker';
  }
  if (body.includes('service temporarily unavailable') || body.includes('site is down for maintenance')) {
    return `maintenance:${url}`;
  }
  return null;
}
