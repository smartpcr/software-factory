import { expect, test } from '@playwright/test';

test.describe('/', () => {
  test('overview renders stats, activity, agents, jump-links', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('main.main .page-eyebrow .num')).toHaveText('00');
    await expect(page.locator('main.main h1.page-title')).toContainText('Atlas factory floor');
    await expect(page.locator('main.main .overview-stats .stat-cell')).toHaveCount(4);
    await expect(page.locator('main.main .activity-feed .activity-feed-row').first()).toBeVisible();
    await expect(page.locator('main.main .agents-list .agent-row').first()).toBeVisible();
    // Jump links — at least 5 (plan, timeline, workstreams, prs, audit).
    await expect(page.locator('main.main .ws-pr-row[class]')).toHaveCount(5);
  });

  test('overview jump-link navigates to /audit', async ({ page }) => {
    await page.goto('/');
    await page.locator('main.main .ws-pr-row .title:has-text("Audit log")').click();
    await expect(page).toHaveURL(/\/audit$/);
    await expect(page.locator('main.main h1.page-title')).toContainText('Audit log');
  });
});
