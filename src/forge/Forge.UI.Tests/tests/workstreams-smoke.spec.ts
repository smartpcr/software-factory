import { expect, test } from '@playwright/test';

// Phase 3.12 — Playwright smoke for /workstreams.
// Covers: render, filter chip, "Sync from epics", and the create flow.

test.describe('/workstreams', () => {
  test('renders stats strip and at least one workstream card', async ({ page }) => {
    await page.goto('/workstreams');
    await expect(page.locator('main.main .workstreams-stats .stat-cell')).toHaveCount(4);
    const cards = page.locator('main.main .workstream-grid .ws-card:not(.ws-card-new)');
    await expect.poll(async () => await cards.count(), { timeout: 5_000 }).toBeGreaterThan(0);
    // CTA card always present.
    await expect(page.locator('main.main .ws-card.ws-card-new')).toBeVisible();
  });

  test('filter chip narrows the grid', async ({ page }) => {
    await page.goto('/workstreams');
    const total = await page.locator('main.main .workstream-grid .ws-card:not(.ws-card-new)').count();
    await page.locator('.filter-chip:has-text("Conflicts")').click();
    const conflicts = await page.locator('main.main .workstream-grid .ws-card:not(.ws-card-new)').count();
    expect(conflicts).toBeLessThan(total);
    expect(conflicts).toBeGreaterThan(0);
  });

  test('Sync from epics fires a toast', async ({ page }) => {
    await page.goto('/workstreams');
    await page.locator('button:has-text("Sync from epics")').click();
    await expect(page.locator('.toast.show')).toBeVisible();
  });

  test('New workstream modal opens, validates, and creates', async ({ page }) => {
    await page.goto('/workstreams');
    // Phase-3 placeholder auth — set the writer header for the API.
    await page.evaluate(() => window.localStorage.setItem('forge.user', 'playwright'));

    await page.locator('.page-head-right button:has-text("New workstream")').click();
    await expect(page.locator('.modal.show')).toBeVisible();

    const name = `pw-${Date.now().toString(36)}`;
    await page.locator('.modal.show input').first().fill(name);
    await page.locator('.modal.show .btn-primary').click();

    // Either the modal closes (success) or an inline error surfaces (network).
    await expect(page.locator('.modal.show')).toHaveCount(0, { timeout: 5_000 });
    await expect(page.locator(`main.main .ws-card .ws-card-name:has-text("${name}")`)).toBeVisible();
  });
});
