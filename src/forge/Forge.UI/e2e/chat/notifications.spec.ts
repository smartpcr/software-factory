import { test, expect, type Page } from '@playwright/test';
import type { ChatConnectionDetails, ChatDestination, ChatDraft, ChatPlugin, ChatSummary } from '../../src/swarm/plugins/chatApi';

const titles = ['BEYOND-AST', '3D-RENDER', 'MCP', 'AGENT MEMORY', 'PR INGESTER'];
async function mockApi(page: Page, owner = true, host = true) {
  const plugins = new Map<string, ChatPlugin>();
  const writes: { path: string; body: any }[] = [];
  const reads: string[] = [];
  let conflict = false;
  let validationFailure = '';
  let links: any[] = [];
  let hostSubscriptions: any[] = [];
  const checks: any[] = [];
  const connections: ChatConnectionDetails[] = ['telegram', 'slack'].map((providerId, i) => ({ id: providerId, providerId, displayName: i ? 'Render workspace' : 'Forge bot',
    enabled: true, revision: 1, state: 'configured', reason: null, installationId: i ? 'T1' : '12345', settingsVersion: 1,
    settings: i ? { appId: 'A1' } : {}, secretReferences: { 'bot-token': { store: 'dpapi', key: 'saved' } }, productIds: ['p', 'q'] }));
  const destinations: ChatDestination[] = [{ id: 'tg-ops', displayName: 'Forge Ops', connectionId: 'telegram', kind: 'forum', channelId: '-100', externalTenantId: '12345', revision: 1, accessState: 'ready' },
    { id: 'slack-render', displayName: '#render', connectionId: 'slack', kind: 'public-channel', channelId: 'C1', externalTenantId: 'T1', revision: 1, accessState: 'ready' }];
  const plugin = (p: string, s: string): ChatPlugin => plugins.get(`${p}:${s}`) ?? { productId: p, spawnId: s, enabled: false, revision: 0, reason: null, subscriptions: [] };
  const summary = (p: string, s: string): ChatSummary => {
    const value = plugin(p, s);
    return { state: value.enabled ? 'ready' : 'off', revision: value.revision, unroutedCount: 0, unroutedReasons: {}, pendingCount: 0, failedCount: 0, sentCount: 0,
      destinations: value.subscriptions.map(sub => { const d = destinations.find(d => d.id === sub.destinationId)!; return { destinationId: d.id, connectionId: d.connectionId, providerId: connections.find(c => c.id === d.connectionId)!.providerId, displayName: d.displayName }; }) };
  };
  await page.route(url => url.pathname.startsWith('/api/'), async route => {
    const req = route.request(); const url = new URL(req.url()); const path = url.pathname;
    const method = req.method(); const body = req.postData() ? req.postDataJSON() : undefined;
    if (method !== 'GET') writes.push({ path, body }); else reads.push(path);
    let json: any = {};
    const row = path.match(/products\/(.*?)\/story-spawns\/(.*?)\/plugins\/chat\/(setup|history)/);
    const connection = path.match(/\/connections\/([^/]+)(.*)/);
    if (row) {
      const [, p, s, action] = row;
      if (action === 'history') json = { unrouted: {}, deliveries: [{ id: 'failed-delivery', connectionId: 'slack', state: 'failed', attempts: 3, lastError: 'slack-membership-required', nextAttemptAt: 0 }] };
      else {
        if (method === 'PUT') {
          if (conflict) { await route.fulfill({ status: 409, json: { title: 'revision-conflict' } }); return; }
          const draft = body as ChatDraft; const old = plugin(p, s);
          plugins.set(`${p}:${s}`, { ...old, enabled: draft.enabled, revision: old.revision + 1, subscriptions: draft.destinations.map((d, i) => ({ ...d, id: `sub-${s}-${i}`, revision: 1, paused: false, eventKindsJson: JSON.stringify(d.eventKinds ?? ['open-questions', 'workstream-stalled']) })) });
        }
        json = { plugin: plugin(p, s), canManage: owner, canManageConnections: host, interactiveAvailable: true, redactedContentAllowed: true, hostReason: null };
      }
    } else if (path.endsWith('/chat/summaries')) {
      const p = path.split('/')[3]; json = titles.map((title, i) => ({ spawnId: 'row-' + i, title, summary: summary(p, 'row-' + i) }));
    } else if (path === '/api/chat/management') json = { canManageConnections: host, canManageHostAlerts: host, hostReason: null, interactiveAvailable: true, hostRedactedContentAllowed: true,
      products: [{ id: 'p', name: 'Product P' }, { id: 'q', name: 'Product Q' }], links, secretStores: [{ id: 'dpapi', available: true, externallyManaged: false }, { id: 'environment', available: true, externallyManaged: true }] };
    else if (path === '/api/chat/connections') json = connections;
    else if (connection) {
      const [, id, rest] = connection; const c = connections.find(c => c.id === id);
      if (!rest) {
        if (method === 'PUT') { const saved = { ...body, id, revision: (c?.revision ?? 0) + 1, state: body.enabled ? 'configured' : 'off' }; if (c) Object.assign(c, saved); else connections.push(saved); json = saved; }
        else json = c;
      } else if (rest === '/health') json = { connectionId: id, revision: c?.revision ?? 1, appliedRevision: c?.revision ?? 1, state: 'ready', reason: null, lastSendAt: null, lastReceiveAt: null,
        credentials: [{ id: 'bot-token', configured: true, store: 'dpapi', externallyManaged: false, available: true }] };
      else if (rest === '/destinations') json = destinations.filter(d => d.connectionId === id);
      else if (rest === '/discovery') json = { succeeded: true, value: { items: [{ address: { externalTenantId: '12345', channelId: '-200' }, displayName: 'New Ops', kind: 'forum' }], nextCursor: null } };
      else if (rest.endsWith('/validate')) {
        json = { succeeded: !validationFailure, value: {}, failure: validationFailure ? { code: validationFailure } : null };
      } else if (rest.endsWith('/checks')) {
        if (method === 'POST') checks.push({ id: body.requestId, state: 'sent', reason: null, createdAt: new Date().toISOString(), sentAt: new Date().toISOString(), replyAt: null, connectionRevision: body.connectionRevision, destinationRevision: body.destinationRevision });
        json = method === 'POST' ? checks.at(-1) : checks;
      } else if (rest.startsWith('/destinations/') && method === 'PUT') { const d = { ...body, id: rest.split('/')[2], connectionId: id, revision: 1, accessState: 'ready' }; destinations.push(d); json = d; }
      else if (rest.startsWith('/credentials/')) { c!.revision++; json = c; }
      else if (rest === '/link-challenge') { links = [{ providerId: id, externalTenantId: body.externalTenantId, externalUserId: body.externalUserId }]; json = { token: 'fixture', expiresInSeconds: 120, deepLink: null, channelCommand: '!forge link fixture' }; }
    } else if (path === '/api/chat/operator-links/self') { links = []; await route.fulfill({ status: 204 }); return; }
    else if (path === '/api/chat/host/subscriptions') json = { subscriptions: hostSubscriptions };
    else if (path.startsWith('/api/chat/host/subscriptions/')) {
      const id = path.split('/').at(-1); if (method === 'DELETE') hostSubscriptions = hostSubscriptions.filter(s => s.id !== id);
      else { hostSubscriptions = hostSubscriptions.filter(s => s.id !== id); hostSubscriptions.push({ ...body, id, revision: 1, eventKindsJson: JSON.stringify(body.eventKinds) }); }
    } else if (path === '/api/chat/host/status') json = { chat: { unrouted: { 'no-destination': 3 }, deliveries: [] }, orphaned: 2, degraded: 0 };
    else if (path === '/api/chat/preview') json = { text: body.contentMode === 'metadata' ? 'Forge notification. Open Forge to review.' : 'An operator response is needed. [redacted] [redacted]' };
    else if (/\/stories\/story-\d\/spawn$/.test(path)) {
      expect(url.searchParams.get('includeChatSummary')).toBe('false');
      const index = Number(path.match(/story-(\d)/)![1]);
      json = { spawn: { id: 'row-' + index, storyId: 'story-' + index, storyTitle: titles[index], phase: index === 2 ? 'done' : 'implement' },
        workstreams: Array.from({ length: index === 0 ? 32 : 3 }, (_, i) => ({ id: 'child-' + i, title: 'Child ' + i, status: 'running', phase: 'implement' })) };
    }
    else if (path.endsWith('/csrf')) json = { token: 'fixture-csrf' };
    else if (path.endsWith('/retry')) json = { state: 'retry' };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json });
  });
  return { writes, reads, setConflict: () => { conflict = true; }, failValidation: (code = 'slack-membership-required') => { validationFailure = code; } };
}
const rowButton = (page: Page, title: string) => page.getByRole('button', { name: `Chat for ${title}`, exact: true });
async function choose(page: Page, provider: string, channel: string, index = 0) {
  await page.getByRole('combobox', { name: 'Provider', exact: true }).nth(index).selectOption(provider);
  await page.getByRole('combobox', { name: 'Channel', exact: true }).nth(index).selectOption(channel);
}
async function enable(page: Page, title: string, providers: [string, string][]) {
  await rowButton(page, title).click(); await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  for (let i = 0; i < providers.length; i++) {
    if (i) await page.getByRole('button', { name: 'Add provider/channel' }).click();
    await choose(page, ...providers[i], i);
  }
  await page.getByRole('button', { name: 'Save and enable' }).click(); await expect(page.getByRole('dialog')).not.toBeVisible();
}

test('five independent rows survive refresh, reorder, filter and product switch; detail shares state', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html');
  await expect(rowButton(page, 'MCP')).toContainText('Off');
  expect(mock.reads.filter(p => p.endsWith('/chat/summaries'))).toHaveLength(1);
  expect(mock.reads.some(p => p.endsWith('/setup'))).toBe(false);
  await enable(page, 'BEYOND-AST', [['telegram', 'tg-ops']]);
  await enable(page, '3D-RENDER', [['slack', 'slack-render']]);
  await enable(page, 'AGENT MEMORY', [['telegram', 'tg-ops'], ['slack', 'slack-render']]);
  await page.reload(); await expect(rowButton(page, 'BEYOND-AST')).toContainText('telegram / Forge Ops');
  await expect(rowButton(page, '3D-RENDER')).toContainText('slack / #render'); await expect(rowButton(page, 'AGENT MEMORY')).toContainText('2 destinations');
  await expect(rowButton(page, 'MCP')).toContainText('Off'); await expect(rowButton(page, 'PR INGESTER')).toContainText('Off');
  await page.screenshot({ path: '../../../.tmp-build/chat-stage5/five-rows.png', fullPage: true });
  await page.getByRole('button', { name: 'Reverse rows' }).click(); await page.getByLabel('Filter rows').fill('3D'); await expect(rowButton(page, '3D-RENDER')).toContainText('Ready');
  await page.getByLabel('Filter rows').fill(''); await page.getByRole('combobox', { name: 'Product', exact: true }).selectOption('q'); await expect(rowButton(page, 'BEYOND-AST')).toContainText('Off');
  await page.getByRole('combobox', { name: 'Product', exact: true }).selectOption('p'); await expect(rowButton(page, 'BEYOND-AST')).toContainText('Ready');
  await page.getByRole('button', { name: 'Detail header', exact: true }).click(); await expect(rowButton(page, 'BEYOND-AST detail')).toContainText('Forge Ops');
  await rowButton(page, 'BEYOND-AST detail').click(); await page.getByRole('checkbox', { name: 'Enable chat remote control' }).uncheck(); await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(rowButton(page, 'BEYOND-AST')).toContainText('Off');
  expect(mock.writes.filter(w => /pause|resume|restart|checks/.test(w.path))).toHaveLength(0);
  await page.screenshot({ path: '../../../.tmp-build/chat-stage5/shared-header.png', fullPage: true });
});

test('cancel, keyboard focus, provider changes and completed rows', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); const trigger = rowButton(page, 'MCP');
  await trigger.focus(); await page.keyboard.press('Enter'); await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check(); await choose(page, 'telegram', 'tg-ops');
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('slack'); await expect(page.getByRole('combobox', { name: 'Channel', exact: true })).toHaveValue('');
  await page.keyboard.press('Escape'); await expect(trigger).toBeFocused(); expect(mock.writes.filter(w => w.path.endsWith('/setup'))).toHaveLength(0);
  await enable(page, 'MCP', [['slack', 'slack-render']]); await expect(trigger).toContainText('Ready');
  expect(mock.writes.some(w => /pause|resume|restart/.test(w.path))).toBe(false);
});

test('copy destinations stays a draft and stale revisions preserve the draft', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); await enable(page, 'BEYOND-AST', [['telegram', 'tg-ops']]);
  await rowButton(page, 'PR INGESTER').click(); await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  await page.getByText('Copy destinations from another row', { exact: true }).click(); await page.getByRole('combobox', { name: 'Source row', exact: true }).selectOption({ label: 'BEYOND-AST' }); await page.getByRole('button', { name: 'Copy into draft' }).click();
  await expect(page.getByRole('combobox', { name: 'Channel', exact: true })).toHaveValue('tg-ops'); await page.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(rowButton(page, 'PR INGESTER')).toContainText('Off');
  mock.setConflict(); await rowButton(page, 'PR INGESTER').click(); await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check(); await choose(page, 'slack', 'slack-render');
  await page.getByRole('button', { name: 'Save and enable' }).click(); await expect(page.getByRole('alert')).toContainText('changed in another window'); await expect(page.getByRole('combobox', { name: 'Channel', exact: true })).toHaveValue('slack-render');
});

test('inline connection and discovery return to the same row without activating it on cancel', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'BEYOND-AST').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check(); await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('telegram');
  await page.getByRole('button', { name: 'Set up connection', exact: true }).click(); await page.getByLabel('Connection name').fill('Second bot'); await page.getByLabel('Bot ID', { exact: true }).fill('22222');
  await page.getByLabel('Bot token', { exact: true }).fill('fixture-secret'); await page.getByRole('checkbox', { name: 'Enable connection', exact: true }).check(); await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Remote control · BEYOND-AST' })).toBeVisible();
  await page.getByRole('button', { name: 'Discover or add channel', exact: true }).click(); await page.getByRole('button', { name: 'Discover channels', exact: true }).click();
  await page.getByLabel('Find channel by name').fill('New'); await page.getByRole('button', { name: 'New Ops', exact: true }).click(); await page.getByRole('button', { name: 'Save channel', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Channel', exact: true })).not.toHaveValue(''); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(rowButton(page, 'BEYOND-AST')).toContainText('Off'); expect(mock.writes.filter(w => w.path.endsWith('/setup') || w.path.endsWith('/checks'))).toHaveLength(0);
});

test('settings repair, independent verification, explicit test send, host alerts and links', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); await page.getByRole('button', { name: 'Chat settings', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Host alerts', exact: true })).toBeVisible(); await expect(page.getByText('Orphaned scopes 2')).toBeVisible();
  await page.getByRole('button', { name: 'Render workspace slack · configured' }).click();
  await expect(page.getByText('Inbound reply', { exact: true })).toBeVisible(); expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(0);
  mock.failValidation(); await page.getByRole('button', { name: 'Validate channel access' }).click(); await expect(page.getByRole('alert')).toContainText('Invite the Slack app');
  expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(0);
  await page.getByRole('button', { name: 'Send test message' }).click(); expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(1);
  await page.getByRole('button', { name: 'Repair / edit' }).nth(1).click(); await expect(page.getByLabel('Workspace ID')).toHaveValue('T1'); await page.getByLabel('Bot token', { exact: true }).fill('replacement-fixture'); await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await page.getByRole('combobox', { name: 'Host provider', exact: true }).selectOption('slack'); await page.getByRole('combobox', { name: 'Host channel', exact: true }).selectOption('slack-render'); await page.getByRole('button', { name: 'Save host alerts' }).click();
  expect(mock.writes.find(w => w.path.startsWith('/api/chat/host/subscriptions/'))?.body.eventKinds).toEqual(['queue-strain', 'human-interaction']);
  await page.getByText('Link or re-link my account', { exact: true }).click(); await page.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack'); await page.getByLabel('Workspace or bot ID').fill('T1'); await page.getByLabel('My chat user ID').fill('U1');
  await expect(page.getByRole('button', { name: 'Create link challenge' })).toBeDisabled(); await page.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check(); await page.getByRole('button', { name: 'Create link challenge' }).click();
  await expect(page.getByRole('button', { name: 'Check link status' })).toBeVisible(); await page.getByLabel('My chat user ID').fill('U2');
  await expect(page.getByRole('button', { name: 'Check link status' })).toHaveCount(0); await expect(page.getByRole('button', { name: 'Create link challenge' })).toBeDisabled();
  await page.getByLabel('My chat user ID').fill('U1'); await page.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check(); await page.getByRole('button', { name: 'Create link challenge' }).click();
  await page.getByRole('button', { name: 'Check link status' }).click(); await expect(page.getByText('slack · T1 · U1 — linked')).toBeVisible(); await page.getByRole('button', { name: 'Revoke my chat links' }).click(); await expect(page.getByText('Not linked', { exact: true })).toBeVisible();
  await page.screenshot({ path: '../../../.tmp-build/chat-stage5/chat-settings.png', fullPage: true });
});

test('read-only roles and narrow viewport keep safe controls and visible footer', async ({ page }) => {
  const mock = await mockApi(page, false, false); await page.setViewportSize({ width: 390, height: 844 }); await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'BEYOND-AST').click();
  await expect(page.getByRole('checkbox', { name: 'Enable chat remote control' })).toBeDisabled(); await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport(); const box = await page.getByRole('dialog').boundingBox(); expect(box!.width).toBeLessThanOrEqual(390);
  await page.screenshot({ path: '../../../.tmp-build/chat-stage5/drawer-mobile.png' }); await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Chat settings', exact: true }).click(); await expect(page.getByText('Host alert permission is required.', { exact: false })).toBeVisible();
  expect(mock.writes.filter(w => w.path.endsWith('/setup'))).toHaveLength(0);
});

for (const [code, message] of [['slack-channel-archived', 'This Slack channel is archived'], ['slack-bot-token-revoked', 'Replace the revoked Slack credential']]) {
  test(`connection repair explains ${code}`, async ({ page }) => {
    const mock = await mockApi(page); mock.failValidation(code); await page.goto('/e2e/chat/fixture.html');
    await page.getByRole('button', { name: 'Chat settings', exact: true }).click(); await page.getByRole('button', { name: 'Render workspace slack · configured' }).click();
    await page.getByRole('button', { name: 'Validate channel access' }).click(); await expect(page.getByRole('alert')).toContainText(message);
    expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(0);
  });
}
