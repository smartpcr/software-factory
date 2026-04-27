import { expect, test } from '@playwright/test';

// Phase 2.12 — timeline smoke: zoom toggle changes density, Now scrolls.

test.describe('/timeline', () => {
  test('renders axis, epic rows and bars', async ({ page }) => {
    await page.goto('/timeline');
    await expect(page.locator('main.main .timeline-axis')).toBeVisible();
    // 4 epics have workstreams in the seed (EPIC-024, 021, 022, 026, 018 = 5)
    const epicRows = page.locator('main.main .timeline-row.epic-row');
    await expect.poll(async () => await epicRows.count(), { timeout: 5_000 }).toBeGreaterThanOrEqual(4);
    const bars = page.locator('main.main .timeline-bar');
    await expect.poll(async () => await bars.count()).toBeGreaterThan(0);
    await expect(page.locator('main.main .timeline-legend')).toBeVisible();
  });

  test('zoom toggle changes the day-column width', async ({ page }) => {
    await page.goto('/timeline');
    const measure = async () => {
      const w = await page.locator('main.main .timeline-axis-day').first().evaluate((el) => el.getBoundingClientRect().width);
      return Math.round(w);
    };
    await page.locator('.timeline-zoom button[data-zoom="week"]').click();
    const week = await measure();

    await page.locator('.timeline-zoom button[data-zoom="day"]').click();
    const day = await measure();
    expect(day).toBeGreaterThan(week);

    await page.locator('.timeline-zoom button[data-zoom="sprint"]').click();
    const sprint = await measure();
    expect(sprint).toBeLessThan(week);
  });

  test('Now button scrolls the wrap to today', async ({ page }) => {
    await page.goto('/timeline');
    // Make the wrap horizontally scrollable, then jump to the far left.
    const wrap = page.locator('main.main .timeline-wrap');
    await wrap.evaluate((el) => { el.scrollLeft = 0; });
    await page.locator('.page-head-right .btn-secondary:has-text("Now")').click();
    // Allow the smooth-scroll to settle.
    await page.waitForTimeout(600);
    const left = await wrap.evaluate((el) => el.scrollLeft);
    expect(left).toBeGreaterThan(0);
  });

  test('filter chip narrows visible bars', async ({ page }) => {
    await page.goto('/timeline');
    // Wait for the API hydration before counting — bars are async.
    await page.locator('main.main .timeline-bar').first().waitFor({ state: 'visible' });
    const total = await page.locator('main.main .timeline-bar').count();
    await page.locator('.timeline-toolbar .filter-chip:has-text("Merging")').click();
    await page.locator('main.main .timeline-bar').first().waitFor({ state: 'visible' });
    const merging = await page.locator('main.main .timeline-bar').count();
    expect(merging).toBeLessThan(total);
    expect(merging).toBeGreaterThan(0);
  });

  test('clicking a bar navigates to /workstreams', async ({ page }) => {
    await page.goto('/timeline');
    await page.locator('main.main .timeline-bar').first().click();
    await expect(page).toHaveURL(/\/workstreams$/);
  });
});
