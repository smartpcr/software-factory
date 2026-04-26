import { expect, test } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Phase 1.14 — fidelity baselines for the Plan & Design board.
// Captures four states from the prototype as baselines, then asserts the
// React render matches each one.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const prototypeUrl = 'file://' + path
  .resolve(__dirname, '../../../../docs/examples/forge-board.html')
  .replace(/\\/g, '/');

const themes = ['dark', 'light'] as const;

async function freezeAnimations(page: import('@playwright/test').Page) {
  await page.addStyleTag({
    content: `* { animation: none !important; transition: none !important; }`,
  });
}

for (const theme of themes) {
  test(`/plan board ${theme}`, async ({ page }) => {
    // Prototype baseline.
    await page.goto(prototypeUrl);
    await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
    await freezeAnimations(page);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);
    await page.locator('main.main .board').waitFor();
    const proto = await page.locator('main.main .board-wrap').screenshot();

    // React render.
    await page.goto('/plan');
    await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
    await freezeAnimations(page);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);
    await page.locator('main.main .board').waitFor();
    const react = await page.locator('main.main .board-wrap').screenshot();

    expect(proto).toMatchSnapshot(`board.${theme}.png`, { maxDiffPixelRatio: 0.10 });
    expect(react).toMatchSnapshot(`board.${theme}.png`, { maxDiffPixelRatio: 0.10 });
  });
}

test('detail panel open on #234', async ({ page }) => {
  await page.goto('/plan');
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.locator('main.main .card[data-id="#234"]').first().click();
  await expect(page.locator('aside.detail-panel.open')).toBeVisible();
  const shot = await page.locator('aside.detail-panel').screenshot();
  expect(shot).toMatchSnapshot(`detail-panel.234.png`, { maxDiffPixelRatio: 0.10 });
});

test('new-card modal open', async ({ page }) => {
  await page.goto('/plan');
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  await page.locator('button.btn-primary:has-text("New item")').click();
  await expect(page.locator('.modal.show')).toBeVisible();
  const shot = await page.locator('.modal.show').screenshot();
  expect(shot).toMatchSnapshot(`new-card-modal.png`, { maxDiffPixelRatio: 0.10 });
});

test('card hover state', async ({ page }) => {
  await page.goto('/plan');
  await freezeAnimations(page);
  await page.waitForLoadState('networkidle');
  const card = page.locator('main.main .card[data-id="#234"]').first();
  await card.hover();
  const shot = await card.screenshot();
  expect(shot).toMatchSnapshot(`card-hover.234.png`, { maxDiffPixelRatio: 0.10 });
});
