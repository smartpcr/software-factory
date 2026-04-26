import { expect, test } from '@playwright/test';

// Phase 1.13 — Playwright DnD smoke.
// Verifies the board renders, the user can drag a card to another column,
// and the policy modal fires for skip-phase moves.

test.describe('/plan board', () => {
  test('renders 5 columns and at least one card per non-empty column', async ({ page }) => {
    await page.goto('/plan');
    const columns = page.locator('main.main .col-head:not(.swimlane-corner)');
    await expect(columns).toHaveCount(5);
    const cards = page.locator('main.main .card');
    await expect(cards.first()).toBeVisible();
  });

  test('clicking a card opens the detail panel', async ({ page }) => {
    await page.goto('/plan');
    const card = page.locator('main.main .card[data-id="#234"]').first();
    await card.scrollIntoViewIfNeeded();
    await card.click();
    await expect(page.locator('aside.detail-panel.open')).toBeVisible();
    await expect(page.locator('aside.detail-panel #detail-id')).toHaveText('#234');
  });

  test('filter chip narrows the board', async ({ page }) => {
    await page.goto('/plan');
    const total = await page.locator('main.main .card').count();
    await page.locator('.filter-chip:has-text("Blocked")').click();
    const blocked = await page.locator('main.main .card').count();
    expect(blocked).toBeLessThan(total);
    expect(blocked).toBeGreaterThan(0);
  });

  test('skip-phase drag fires the confirm modal', async ({ page }) => {
    await page.goto('/plan');
    const source = page.locator('main.main .card[data-id="#831"]').first();
    const target = page.locator('main.main .swimlane-cell[data-epic="EPIC-024"][data-col="build"]').first();
    await source.scrollIntoViewIfNeeded();
    await target.scrollIntoViewIfNeeded();

    // dnd-kit's PointerSensor needs pointer events with movement past
    // activationConstraint.distance (4px in our config) before a drag starts.
    const srcBox = (await source.boundingBox())!;
    const tgtBox = (await target.boundingBox())!;
    const sx = srcBox.x + srcBox.width / 2;
    const sy = srcBox.y + srcBox.height / 2;
    const tx = tgtBox.x + tgtBox.width / 2;
    const ty = tgtBox.y + tgtBox.height / 2;

    await page.mouse.move(sx, sy);
    await page.mouse.down();
    await page.mouse.move(sx + 8, sy + 8, { steps: 4 });
    await page.mouse.move(tx, ty, { steps: 12 });
    await page.mouse.up();

    // The skip-phase modal must open; the test proves both DnD wiring AND policy.
    await expect(page.locator('.modal.show')).toBeVisible({ timeout: 5_000 });
    await expect(page.locator('.modal.show .modal-title')).toHaveText('Skip phase?');
  });
});
