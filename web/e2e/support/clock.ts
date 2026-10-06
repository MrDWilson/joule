import type { Page } from '@playwright/test';

/** Minutes since midnight in London (the demo's and the suite's timezone) at `ms`. */
export function londonMinutes(ms: number): number {
  const [hour, minute] = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/London', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
    .format(new Date(ms))
    .split(':')
    .map(Number);
  return hour * 60 + minute;
}

/** The London calendar date (YYYY-MM-DD) at `ms`. */
export function londonDate(ms: number): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/London' }).format(new Date(ms));
}

/**
 * For a test whose fixture needs some of today to have passed ("today so far" charts): when the suite runs earlier in the
 * London day than `minutes` after midnight, the browser's clock is moved on to that time (the demo server keeps real time);
 * otherwise it is left alone. Returns the browser's "now" for building fixtures, which keeps moving with real time.
 */
export async function todayAtLeast(page: Page, minutes: number): Promise<() => number> {
  const shift = Math.max(0, minutes - londonMinutes(Date.now())) * 60_000;
  if (shift > 0) await page.clock.setSystemTime(Date.now() + shift);
  return () => Date.now() + shift;
}
