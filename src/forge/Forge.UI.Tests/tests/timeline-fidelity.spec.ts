import { expect, test, type Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Phase 2.13 — fidelity baselines for the timeline.
// The prototype hard-codes a 60px (week) zoom and renders today as
// the actual today, which means the bars shift left/right with calendar
// time. To keep baselines stable we snap the prototype's `dayW` to each
// zoom value and clip the visible region after that.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const prototypeUrl = 'file://' + path
  .resolve(__dirname, '../../../../docs/examples/forge-board.html')
  .replace(/\\/g, '/');

const themes = ['dark', 'light'] as const;
const zooms: Array<{ name: 'day' | 'week' | 'sprint'; dayW: number }> = [
  { name: 'day',    dayW: 90 },
  { name: 'week',   dayW: 60 },
  { name: 'sprint', dayW: 28 },
];

async function freezeAnimations(page: Page) {
  await page.addStyleTag({
    content: `* { animation: none !important; transition: none !important; }`,
  });
}

async function gotoPrototypeTimeline(page: Page, theme: 'dark' | 'light', dayW: number) {
  await page.goto(prototypeUrl);
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  // Switch to the timeline view, then re-render with the given dayW.
  await page.evaluate((w) => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const win = window as any;
    if (typeof win.switchView === 'function') win.switchView('timeline');
    // Patch renderTimeline's hard-coded dayW by mutating the style.
    const tl = document.getElementById('timeline');
    if (tl) tl.style.setProperty('--day-w', `${w}px`);
  }, dayW);
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

async function gotoReactTimeline(page: Page, theme: 'dark' | 'light', zoom: 'day' | 'week' | 'sprint') {
  await page.goto('/timeline');
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  await page.locator(`.timeline-zoom button[data-zoom="${zoom}"]`).click();
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

for (const theme of themes) {
  for (const zoom of zooms) {
    test(`/timeline ${zoom.name} ${theme}`, async ({ page }) => {
      await gotoPrototypeTimeline(page, theme, zoom.dayW);
      const proto = await page.locator('.timeline-wrap').screenshot();

      await gotoReactTimeline(page, theme, zoom.name);
      const react = await page.locator('main.main .timeline-wrap').screenshot();

      // Tolerate moderate diff: today's calendar position drifts day-to-day,
      // and bar pixel positions follow it. The structure must match.
      expect(proto).toMatchSnapshot(`timeline.${zoom.name}.${theme}.png`, { maxDiffPixelRatio: 0.20 });
      expect(react).toMatchSnapshot(`timeline.${zoom.name}.${theme}.png`, { maxDiffPixelRatio: 0.20 });
    });
  }
}
