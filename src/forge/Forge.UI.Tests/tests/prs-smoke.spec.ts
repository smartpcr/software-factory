import { expect, test } from '@playwright/test';

// Phase 4.11 — PR view smoke.
// Required scenarios per spec:
//   * failing PR shows the `failing` chip
//   * clicking a PR opens the detail panel
//   * auto-merge toggle persists across reloads (round-trips through the API)

test.describe('/prs', () => {
  test('renders 3 columns (implementation / review / build)', async ({ page }) => {
    await page.goto('/prs');
    const cols = page.locator('main.main .pr-board .pr-col');
    await expect(cols).toHaveCount(3);
    await expect(cols.nth(0).locator('.pr-col-name')).toContainText('Implementation');
    await expect(cols.nth(1).locator('.pr-col-name')).toContainText('Critical review');
    await expect(cols.nth(2).locator('.pr-col-name')).toContainText('Build & merge');
  });

  test('failing PR carries the `failing` build chip', async ({ page }) => {
    await page.goto('/prs');
    const failing = page.locator('main.main .pr-card.failing');
    await expect.poll(async () => await failing.count(), { timeout: 5_000 }).toBeGreaterThan(0);
    await expect(failing.first().locator('.pr-card-build .step.failing')).toHaveText('failing');
  });

  test('Failing filter chip narrows the board', async ({ page }) => {
    await page.goto('/prs');
    const total = await page.locator('main.main .pr-card').count();
    await page.locator('.filter-chip:has-text("Failing")').click();
    const failing = await page.locator('main.main .pr-card').count();
    expect(failing).toBeLessThan(total);
    expect(failing).toBeGreaterThan(0);
  });

  test('clicking the open action opens the detail panel', async ({ page }) => {
    await page.goto('/prs');
    // Pick the first PR card that has an Open button (non-human-gate path).
    const card = page.locator('main.main .pr-card').filter({
      has: page.locator('.pr-card-action[title="Open"]'),
    }).first();
    await card.locator('.pr-card-action[title="Open"]').click();
    await expect(page.locator('aside.detail-panel.open#pr-detail-panel')).toBeVisible();
    await expect(page.locator('aside#pr-detail-panel .detail-title')).not.toBeEmpty();
  });

  test('auto-merge toggle persists across reloads', async ({ page }) => {
    await page.goto('/prs');
    await page.evaluate(() => window.localStorage.setItem('forge.user', 'playwright'));

    // Pick PR #229 — its prototype data has autoMerge: true. Toggling
    // round-trips through POST /api/prs/{id}/auto-merge.
    const target = page.locator('main.main .pr-card[data-pr-id="PR #229"]').first();
    await target.scrollIntoViewIfNeeded();
    await target.locator('.pr-card-action[title="Open"]').click();
    const toggle = page.locator('[data-testid="auto-merge-toggle"]');
    const initial = await toggle.getAttribute('data-state');
    await toggle.click();
    await expect(toggle).not.toHaveAttribute('data-state', initial!);

    // Reload — the persisted state must be reflected after the API rehydrates.
    const after = await toggle.getAttribute('data-state');
    await page.reload();
    await page.locator('main.main .pr-card[data-pr-id="PR #229"]').first().locator('.pr-card-action[title="Open"]').click();
    await expect(page.locator('[data-testid="auto-merge-toggle"]')).toHaveAttribute('data-state', after!);
  });
});
