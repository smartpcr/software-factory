import { expect, test } from '@playwright/test';

// Crew agents render real image avatars + capability chips.
test('overview agent rows show avatar + capabilities', async ({ page }) => {
  await page.goto('/');

  // First agent row should have an <img.agent-avatar> with a non-empty src.
  const firstAvatar = page.locator('main.main .agents-list .agent-row .agent-avatar').first();
  await expect(firstAvatar).toBeVisible();
  const src = await firstAvatar.getAttribute('src');
  expect(src, 'avatar src must be set').toBeTruthy();
  // Vite serves dev assets from /src/assets/crews and bundles them under /assets in prod.
  expect(src!).toMatch(/(crews|assets)/);

  // At least one capability chip per row.
  const caps = page.locator('main.main .agents-list .agent-row .agent-cap');
  expect(await caps.count(), 'crew rows should expose capability chips').toBeGreaterThan(5);
});

test('Ne Zha is on the crew with eng role + auth capability', async ({ page }) => {
  await page.goto('/');
  const row = page.locator('main.main .agents-list .agent-row', { hasText: 'Ne Zha' }).first();
  await expect(row).toBeVisible();
  await expect(row.locator('.role-tag')).toHaveText('eng');
  await expect(row.locator('.agent-cap', { hasText: 'auth' })).toBeVisible();
});
