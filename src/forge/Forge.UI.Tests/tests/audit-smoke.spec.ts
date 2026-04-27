import { expect, test } from '@playwright/test';

// Phase 6.13 — exercise the create-card → audit visibility path end-to-end.

test('writes show up on /audit', async ({ page, request }) => {
  // Trigger a write through the API directly so audit fires deterministically.
  // (Skipping localStorage write — Playwright forbids it before page.goto.)
  const baseURL = test.info().project.use.baseURL;
  const apiBase = baseURL?.replace('5181', '5180') ?? 'http://localhost:5180';
  const resp = await request.post(`${apiBase}/api/prs/PR%20%23234/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': 'playwright-audit' },
    data: { autoMerge: true },
  });
  const body = await resp.text();
  expect(resp.status(), `body=${body}`).toBeLessThan(300);

  // Hit the audit endpoint directly to verify the middleware ran.
  const auditResp = await request.get(`${apiBase}/api/audit?take=20`);
  const auditBody = await auditResp.text();
  console.log('[audit-smoke] /api/audit body=', auditBody);
  expect(auditBody, '/api/audit must contain the playwright-audit user').toContain('playwright-audit');

  await page.goto('/audit');
  await expect(page.locator('main.main h1.page-title')).toContainText('Audit log');
  // Wait for at least one row — the page polls every 5s, so this can take a beat.
  const rows = page.locator('main.main table tbody tr');
  await expect.poll(async () => await rows.count(), { timeout: 8_000 }).toBeGreaterThan(0);
  // The write we just made should be near the top.
  const recent = page.locator('main.main table tbody tr', { hasText: 'playwright-audit' });
  await expect(recent.first()).toContainText('POST');

  // Cleanup — restore PR #234 autoMerge=false so prs-fidelity baselines stay stable.
  await request.post(`${apiBase}/api/prs/PR%20%23234/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': 'playwright-audit' },
    data: { autoMerge: false },
  });
});
