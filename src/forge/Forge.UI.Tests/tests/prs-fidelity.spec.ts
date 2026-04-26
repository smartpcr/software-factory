import { expect, test, type Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Phase 4.12 — PR fidelity baselines.
// Targets every variant the prototype renders from the seed:
//   * PR #234 — running build
//   * PR #241 — queued build + impl strip
//   * PR #237 — running near-end build + approved review
//   * PR #240 — passing build + changes verdict + human-gate
//   * PR #229 — passing build + approved + auto-merge armed
//   * PR #237b — passing build + changes + conflict + human-gate
//   * PR #231 — failing build + pending review

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const prototypeUrl = 'file://' + path
  .resolve(__dirname, '../../../../docs/examples/forge-board.html')
  .replace(/\\/g, '/');

const themes = ['dark', 'light'] as const;
const variants = [
  'PR #234', 'PR #241', 'PR #237', 'PR #240',
  'PR #229', 'PR #237b', 'PR #231',
];

async function freezeAnimations(page: Page) {
  await page.addStyleTag({
    content: `* { animation: none !important; transition: none !important; }`,
  });
}

async function gotoPrototype(page: Page, theme: 'dark' | 'light') {
  await page.goto(prototypeUrl);
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  await page.evaluate(() => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const win = window as any;
    if (typeof win.switchView === 'function') win.switchView('prs');
  });
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

async function gotoReact(page: Page, theme: 'dark' | 'light') {
  await page.goto('/prs');
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

test.describe('/prs fidelity', () => {
  for (const theme of themes) {
    test(`board ${theme}`, async ({ page }) => {
      await gotoPrototype(page, theme);
      const proto = await page.locator('main.main .pr-board').screenshot();
      await gotoReact(page, theme);
      const react = await page.locator('main.main .pr-board').screenshot();
      expect(proto).toMatchSnapshot(`board.${theme}.png`, { maxDiffPixelRatio: 0.15 });
      expect(react).toMatchSnapshot(`board.${theme}.png`, { maxDiffPixelRatio: 0.15 });
    });
  }

  for (const variant of variants) {
    test(`card variant ${variant}`, async ({ page }) => {
      await gotoReact(page, 'dark');
      const card = page.locator(`main.main .pr-card[data-pr-id="${variant}"]`).first();
      await card.scrollIntoViewIfNeeded();
      const shot = await card.screenshot();
      const slug = variant.replace(/[^a-z0-9]+/gi, '-').toLowerCase();
      expect(shot).toMatchSnapshot(`card-${slug}.png`, { maxDiffPixelRatio: 0.10 });
    });
  }
});
