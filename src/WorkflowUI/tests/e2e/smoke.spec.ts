import { test, expect } from '@playwright/test';

test.describe('Smoke tests', () => {
  const consoleErrors: string[] = [];

  test.beforeEach(async ({ page }) => {
    consoleErrors.length = 0;
    page.on('pageerror', (error) => {
      consoleErrors.push(error.message);
    });
  });

  test.afterEach(() => {
    expect(consoleErrors, 'No JS errors expected on page').toEqual([]);
  });

  test('renders Workflow UI heading', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Workflow UI');
  });

  test('GET /api/health returns 200 with status ok', async ({ request }) => {
    const response = await request.get('http://localhost:5226/api/health');
    expect(response.status()).toBe(200);
    const body = await response.json();
    expect(body.status).toBe('ok');
  });

  test('GET /nonexistent returns 404', async ({ request }) => {
    const response = await request.get('http://localhost:5226/api/nonexistent');
    expect(response.status()).toBe(404);
  });
});
