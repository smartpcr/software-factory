import { expect, test, type Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Phase 3.13 — fidelity baselines for /workstreams.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const prototypeUrl = 'file://' + path
  .resolve(__dirname, '../../../../docs/examples/forge-board.html')
  .replace(/\\/g, '/');

const themes = ['dark', 'light'] as const;

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
    if (typeof win.switchView === 'function') win.switchView('workstreams');
  });
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

async function gotoReact(page: Page, theme: 'dark' | 'light') {
  await page.goto('/workstreams');
  await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
}

for (const theme of themes) {
  test(`/workstreams stats strip ${theme}`, async ({ page }) => {
    await gotoPrototype(page, theme);
    const proto = await page.locator('main.main .workstreams-stats').screenshot();
    await gotoReact(page, theme);
    const react = await page.locator('main.main .workstreams-stats').screenshot();
    expect(proto).toMatchSnapshot(`stats.${theme}.png`, { maxDiffPixelRatio: 0.10 });
    expect(react).toMatchSnapshot(`stats.${theme}.png`, { maxDiffPixelRatio: 0.10 });
  });

  test(`/workstreams grid ${theme}`, async ({ page }) => {
    await gotoPrototype(page, theme);
    const proto = await page.locator('main.main .workstream-grid-wrap').screenshot();
    await gotoReact(page, theme);
    const react = await page.locator('main.main .workstream-grid-wrap').screenshot();
    expect(proto).toMatchSnapshot(`grid.${theme}.png`, { maxDiffPixelRatio: 0.15 });
    expect(react).toMatchSnapshot(`grid.${theme}.png`, { maxDiffPixelRatio: 0.15 });
  });
}

test('new-workstream modal open', async ({ page }) => {
  await page.goto('/workstreams');
  await freezeAnimations(page);
  await page.locator('button:has-text("New workstream")').click();
  await expect(page.locator('.modal.show')).toBeVisible();
  const shot = await page.locator('.modal.show').screenshot();
  expect(shot).toMatchSnapshot('new-workstream-modal.png', { maxDiffPixelRatio: 0.10 });
});

test('conflicts filter active', async ({ page }) => {
  await page.goto('/workstreams');
  await freezeAnimations(page);
  await page.locator('.filter-chip:has-text("Conflicts")').click();
  const shot = await page.locator('main.main .workstream-grid-wrap').screenshot();
  expect(shot).toMatchSnapshot('grid.conflicts.png', { maxDiffPixelRatio: 0.15 });
});
