import { test, expect } from '@playwright/test';

// Diagnostic: visit /plan with the Azure Local product active, dump what the
// board actually contains. Used to verify that user's "/plan empty" report
// is real / which step in the chain breaks.

test('debug — /plan content for Azure Local product', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('forge.activeProductId', 'update');
  });

  const consoleLogs: string[] = [];
  page.on('console', (msg) => consoleLogs.push(`[${msg.type()}] ${msg.text()}`));
  page.on('pageerror', (err) => consoleLogs.push(`[pageerror] ${err.message}`));

  const apiCalls: { url: string; status: number; size: number }[] = [];
  page.on('response', async (resp) => {
    if (resp.url().includes('/api/state')) {
      let size = 0;
      try { size = (await resp.body()).length; } catch { /* ignore */ }
      apiCalls.push({ url: resp.url(), status: resp.status(), size });
    }
  });

  await page.goto('/plan', { waitUntil: 'networkidle' });

  // Wait for the board to either render or surface its empty state.
  await page.waitForTimeout(1500);

  const cardCount = await page.locator('.board .card, .col .card, [data-card-id]').count();
  const emptyStateVisible = await page.locator('text=No cards yet').isVisible().catch(() => false);
  const swimlaneCount = await page.locator('.swimlane, [class*="swimlane"]').count();
  const colHeads = await page.locator('.col-head').count();

  // Read the active product from localStorage on the page.
  const activeProduct = await page.evaluate(() => localStorage.getItem('forge.activeProductId'));

  console.log('========== /plan diagnostic ==========');
  console.log('activeProductId:', activeProduct);
  console.log('api/state calls:', JSON.stringify(apiCalls, null, 2));
  console.log('cardCount in DOM:', cardCount);
  console.log('emptyStateVisible:', emptyStateVisible);
  console.log('swimlaneCount:', swimlaneCount);
  console.log('colHeads:', colHeads);
  console.log('console logs:');
  for (const l of consoleLogs.slice(0, 20)) console.log('  ', l);
  console.log('======================================');

  // Capture a screenshot for visual confirmation.
  await page.screenshot({ path: 'plan-azure-local.png', fullPage: false });

  // Don't fail — this is a diagnostic.
  expect(apiCalls.length).toBeGreaterThan(0);
});
