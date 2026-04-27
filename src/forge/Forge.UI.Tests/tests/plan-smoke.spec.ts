import { expect, test } from '@playwright/test';

// Phase 0.16 — Playwright smoke for the /plan route.
// Phases 1+ will replace the placeholder body with the full board, but
// the shell (topbar / sidebar / page-head) must already render.

test.describe('/plan', () => {
  test('renders the shell and page head', async ({ page }) => {
    await page.goto('/plan');

    await expect(page.locator('header.topbar .brand-name')).toHaveText('Forge');
    await expect(page.locator('aside.sidebar .nav-item.active .label')).toHaveText('Plan & Design');
    await expect(page.locator('main.main .page-eyebrow .num')).toHaveText('01');
    await expect(page.locator('main.main h1.page-title')).toContainText('Sprint 14 board');
    // Phase 1.11 wires the sprint pill to the real burn-down, so the values
    // are computed from the seed data (not the prototype's hard-coded "34 / 89").
    await expect(page.locator('main.main h1.page-title .sprint-pill')).toContainText(/\d+ \/ \d+ pts/);
  });

  test('routes between views', async ({ page }) => {
    await page.goto('/plan');
    await page.locator('aside.sidebar a.nav-item:has(.label:text("Timeline"))').click();
    await expect(page).toHaveURL(/\/timeline$/);
    await expect(page.locator('main.main .page-eyebrow .num')).toHaveText('02');

    await page.locator('aside.sidebar a.nav-item:has(.label:text("Workstreams"))').click();
    await expect(page).toHaveURL(/\/workstreams$/);
    await expect(page.locator('main.main .page-eyebrow .num')).toHaveText('03');

    await page.locator('aside.sidebar a.nav-item:has(.label:text("Pull Requests"))').click();
    await expect(page).toHaveURL(/\/prs$/);
    await expect(page.locator('main.main .page-eyebrow .num')).toHaveText('04');
  });
});
