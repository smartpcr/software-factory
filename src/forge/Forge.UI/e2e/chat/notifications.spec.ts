import { test, expect, type Page } from '@playwright/test';

async function mockApi(page: Page, canManage = true) {
  let persisted = { enabled: false, revision: 0, state: 'off', reason: null as string | null,
    destinationId: null as string | null, connectionId: null as string | null, canManage, lastSentAt: null as string | null,
    preview: 'Forge · Open questions\n\nOpen questions were raised for this story. Open Forge to review the current status.\n\nOpen on Forge host: http://localhost:5181/workstreams/story-p' };
  const writes: { path: string; body: Record<string, unknown> }[] = [];
  let conflict = false;
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const request = route.request(); const path = new URL(request.url()).pathname;
    let json: unknown = {};
    if (path.endsWith('/notification-setup')) {
      if (request.method() === 'PUT') {
        writes.push({ path, body: request.postDataJSON() });
        if (conflict) { await route.fulfill({ status: 409, json: { title: 'revision-conflict' } }); return; }
        const body = request.postDataJSON() as { enabled: boolean; destinationId: string | null };
        persisted = { ...persisted, ...body, connectionId: body.enabled ? 'test-bot' : null,
          state: body.enabled ? 'ready' : 'off', revision: persisted.revision + 1, lastSentAt: '2026-09-10T17:20:00Z' };
      }
      json = path.includes('/products/q/') ? { ...persisted, enabled: false, state: 'off', revision: 0 } : persisted;
    } else if (path.endsWith('/connections')) json = [{ id: 'test-bot', providerId: 'telegram', displayName: 'Fresh test bot', enabled: true, state: 'configured', reason: null }];
    else if (path.endsWith('/destinations')) json = [{ id: 'test-channel', connectionId: 'test-bot', displayName: 'Empty test channel', kind: 'supergroup' }];
    else if (path.endsWith('/csrf')) json = { token: 'fixture-csrf' };
    else if (path.endsWith('/spawn')) { await route.fulfill({ status: 404, json: {} }); return; }
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json });
  });
  return { writes, setConflict: () => { conflict = true; } };
}

test('cancel and Escape discard drafts; Save and reload preserve only the chosen row', async ({ page }) => {
  const mock = await mockApi(page);
  await page.goto('/e2e/chat/fixture.html');
  const first = page.getByRole('button', { name: 'Chat for Build the Telegram notification slice' });
  await expect(first).toContainText('Off');
  await first.click();
  await page.getByRole('switch', { name: 'Enable notifications' }).check();
  await page.getByLabel('Provider / connection').selectOption('test-bot');
  await page.getByLabel('Channel', { exact: true }).selectOption('test-channel');
  await expect(page.getByText('MESSAGE PREVIEW · METADATA ONLY')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect(mock.writes).toHaveLength(0); await expect(first).toContainText('Off');
  await first.click(); await expect(page.getByRole('switch')).not.toBeChecked();
  await page.getByRole('switch').check(); await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible(); expect(mock.writes).toHaveLength(0);
  await first.click(); await page.getByRole('switch').check();
  await page.getByLabel('Provider / connection').selectOption('test-bot');
  await page.getByLabel('Channel', { exact: true }).selectOption('test-channel');
  await page.screenshot({ path: '../../../.tmp-build/chat-stage1s/chat-panel.png' });
  await page.getByRole('button', { name: 'Save notifications' }).click();
  await expect(first).toContainText('Ready'); expect(mock.writes).toHaveLength(1);
  expect(mock.writes[0]).toEqual({ path: '/api/products/p/story-spawns/shared/plugins/chat/notification-setup',
    body: { expectedRevision: 0, enabled: true, destinationId: 'test-channel', activateNewRun: false } });
  await page.reload(); await expect(first).toContainText('Ready');
  await expect(page.getByRole('button', { name: 'Chat for Another product, same spawn ID' })).toContainText('Off');
  await first.click(); await expect(page.getByRole('switch')).toBeChecked();
  await expect(page.getByLabel('Channel', { exact: true })).toHaveValue('test-channel');
  await page.getByRole('switch').uncheck(); await page.getByRole('button', { name: 'Save notifications' }).click();
  await expect(first).toContainText('Off'); expect(mock.writes).toHaveLength(2);
});

test('readers see status and preview with editing disabled', async ({ page }) => {
  const mock = await mockApi(page, false); await page.goto('/e2e/chat/fixture.html');
  await page.getByRole('button', { name: 'Chat for Build the Telegram notification slice' }).click();
  await expect(page.getByText('A product owner can change these settings.')).toBeVisible();
  await expect(page.getByRole('switch')).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Save notifications' })).toBeDisabled();
  expect(mock.writes).toHaveLength(0);
});

test('stale revision keeps the draft open with an actionable error', async ({ page }) => {
  const mock = await mockApi(page); mock.setConflict(); await page.goto('/e2e/chat/fixture.html');
  await page.getByRole('button', { name: 'Chat for Build the Telegram notification slice' }).click();
  await page.getByRole('switch').check(); await page.getByLabel('Provider / connection').selectOption('test-bot');
  await page.getByLabel('Channel', { exact: true }).selectOption('test-channel');
  await page.getByRole('button', { name: 'Save notifications' }).click();
  await expect(page.getByRole('alert')).toContainText('changed in another window');
  await expect(page.getByRole('switch')).toBeChecked();
});

test('the dialog fits a narrow viewport and keeps its actions visible', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 }); await mockApi(page);
  await page.goto('/e2e/chat/fixture.html');
  await page.getByRole('button', { name: 'Chat for Build the Telegram notification slice' }).click();
  await expect(page.getByRole('button', { name: 'Save notifications' })).toBeInViewport();
  await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport();
  const box = await page.getByRole('dialog').boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.width).toBeLessThanOrEqual(390);
});
