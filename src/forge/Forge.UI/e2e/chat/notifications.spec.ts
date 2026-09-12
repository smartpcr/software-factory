import { test, expect, type Page } from '@playwright/test';
import type { ChatConnectionDetails, ChatDestination, ChatDraft, ChatPlugin, ChatSummary } from '../../src/swarm/plugins/chatApi';

const titles = ['BEYOND-AST', '3D-RENDER', 'MCP', 'AGENT MEMORY', 'PR INGESTER'];
async function mockApi(page: Page, owner = true, host = true) {
  const plugins = new Map<string, ChatPlugin>();
  const writes: { path: string; body: any }[] = [];
  const reads: string[] = [];
  let conflict = false;
  let hostReason: string | null = null;
  let validationFailure = '';
  let slackLinkConfirmationAvailable = true;
  let authorizationAvailable = true;
  let telegramConfigured = false;
  let managerFailure = '';
  let slackSetupFailure = '';
  let authorizationTimeOffset = 0;
  let authorizationDecision = 'waiting';
  const authorizations: any[] = [];
  let links: any[] = [];
  let recentLinkTargets: any[] = [];
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
        json = { plugin: plugin(p, s), canManage: owner, canManageConnections: host, interactiveAvailable: true, redactedContentAllowed: true, hostReason };
      }
    } else if (path.endsWith('/chat/summaries')) {
      const p = path.split('/')[3]; json = titles.map((title, i) => ({ spawnId: 'row-' + i, title, summary: summary(p, 'row-' + i) }));
    } else if (path === '/api/chat/management') json = { canManageConnections: host, canManageHostAlerts: host, hostReason, interactiveAvailable: !hostReason, hostRedactedContentAllowed: true,
      slackLinkConfirmationAvailable: !hostReason && slackLinkConfirmationAvailable, recentLinkTargets,
      products: [{ id: 'p', name: 'Product P' }, { id: 'q', name: 'Product Q' }], links, secretStores: [{ id: 'dpapi', available: true, externallyManaged: false }, { id: 'environment', available: true, externallyManaged: true }] };
    else if (path === '/api/chat/authorization') {
      if (method === 'GET') json = ['slack', 'telegram'].map(providerId => {
        const available = authorizationAvailable || (providerId === 'telegram' && telegramConfigured);
        return { providerId, available, reason: available ? null : 'authorization-registration-required', canConfigure: !available };
      });
      else {
        json = { id: 'flow-' + authorizations.length, providerId: body.providerId, status: 'waiting', expiresAt: new Date(Date.now() + authorizationTimeOffset + 600000).toISOString(),
          authorizationUrl: body.providerId === 'slack' ? 'https://slack.com/oauth/v2/authorize?fixture=1' : 'https://t.me/newbot/fixture/fixture_bot',
          accountName: null, installationId: null, reason: null, connectionId: null };
        authorizations.push(json);
      }
    } else if (path === '/api/chat/authorization/slack/setup') {
      json = { id: 'flow-' + authorizations.length, providerId: 'slack', status: 'waiting', setupMode: 'slack-cli',
        expiresAt: new Date(Date.now() + authorizationTimeOffset + 600000).toISOString(), authorizationUrl: 'https://app.slack.com/client/',
        approvalRequest: '/slackauthticket fixture_approval_request', accountName: null, installationId: null, reason: null, connectionId: null };
      authorizations.push(json);
    } else if (path === '/api/chat/authorization/telegram/manager') {
      if (managerFailure) { await route.fulfill({ status: 409, json: { title: managerFailure } }); return; }
      telegramConfigured = true;
      json = { username: 'FixtureManagerBot' };
    } else if (path.startsWith('/api/chat/authorization/')) {
      const flow = authorizations.find(a => a.id === path.split('/')[4]);
      if (method === 'DELETE') flow.status = 'cancelled';
      if (path.endsWith('/poll')) {
        flow.status = authorizationDecision;
        if (flow.status === 'approved') { flow.accountName = flow.providerId === 'slack' ? 'Approved workspace' : '@approved_bot'; flow.installationId = flow.providerId === 'slack' ? 'T222' : '222'; flow.authorizationUrl = null; }
        if (flow.status === 'failed') { flow.reason = 'authorization-denied'; flow.authorizationUrl = null; }
      }
      if (path.endsWith('/slack/approve')) {
        flow.status = slackSetupFailure ? 'failed' : 'approved'; flow.reason = slackSetupFailure || null;
        flow.appId = 'A222'; flow.accountName = 'Approved workspace'; flow.installationId = 'T222'; flow.approvalRequest = null; flow.authorizationUrl = null;
      }
      if (path.endsWith('/save')) {
        flow.status = 'saved'; flow.connectionId = 'authorized-' + flow.id;
        connections.push({ id: flow.connectionId, providerId: flow.providerId, displayName: body.displayName, installationId: flow.installationId,
          enabled: true, revision: 1, settingsVersion: 1, state: 'configured', settings: {}, secretReferences: {}, productIds: body.productIds });
      }
      json = flow;
    }
    else if (path === '/api/chat/connections') json = connections;
    else if (connection) {
      const [, id, rest] = connection; const c = connections.find(c => c.id === id);
      if (!rest) {
        if (method === 'PUT') { const saved = { ...body, id, revision: (c?.revision ?? 0) + 1, state: body.enabled ? 'configured' : 'off' }; if (c) Object.assign(c, saved); else connections.push(saved); json = saved; }
        else json = c;
      } else if (rest === '/health') json = { connectionId: id, revision: c?.revision ?? 1, appliedRevision: c?.revision ?? 1, state: 'ready', reason: null, lastSendAt: null, lastReceiveAt: null,
        credentials: [{ id: 'bot-token', configured: true, store: 'dpapi', externallyManaged: false, available: true }] };
      else if (rest === '/destinations') json = destinations.filter(d => d.connectionId === id);
      else if (rest === '/link-destinations') json = destinations.filter(d => d.connectionId === id);
      else if (rest === '/link-confirmation') {
        if (method === 'DELETE') { await route.fulfill({ status: 204 }); return; }
        json = { expiresInSeconds: 120, deepLink: 'https://slack.com/app_redirect?team=T1&channel=C1', channelName: '#render' };
      }
      else if (rest === '/discovery') json = { succeeded: true, value: { items: [{ address: { externalTenantId: '12345', channelId: '-200' }, displayName: 'New Ops', kind: 'forum' }], nextCursor: null } };
      else if (rest.endsWith('/validate')) {
        json = { succeeded: !validationFailure, value: {}, failure: validationFailure ? { code: validationFailure } : null };
      } else if (rest.endsWith('/checks')) {
        if (method === 'POST') checks.push({ id: body.requestId, state: 'sent', reason: null, createdAt: new Date().toISOString(), sentAt: new Date().toISOString(), replyAt: null, connectionRevision: body.connectionRevision, destinationRevision: body.destinationRevision });
        json = method === 'POST' ? checks.at(-1) : checks;
      } else if (rest.startsWith('/destinations/') && method === 'PUT') { const d = { ...body, id: rest.split('/')[2], connectionId: id, revision: 1, accessState: 'ready' }; destinations.push(d); json = d; }
      else if (rest.startsWith('/credentials/')) { c!.revision++; json = c; }
      else if (rest === '/link-challenge') {
        if (id === 'telegram') json = { token: 'F'.repeat(64), expiresInSeconds: 120, deepLink: 'https://t.me/forge_fixture_bot?start=' + 'F'.repeat(64), channelCommand: null };
        else { links = [{ providerId: id, externalTenantId: body.externalTenantId, externalUserId: body.externalUserId }]; json = { token: 'fixture', expiresInSeconds: 120, deepLink: null, channelCommand: '!forge link fixture' }; }
      }
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
    else if (path === '/api/providers') json = ['ado', 'github'].map(kind => ({ kind, ok: true, message: 'Available', lastSync: null, lastErrorCount: 0 }));
    else if (/^\/api\/auth\/(ado|github)\/status$/.test(path)) json = { kind: path.split('/')[3], connected: false };
    else if (path === '/api/me/settings') json = {};
    else if (path === '/api/admin/db/stats') json = { fileBytes: 1024, transcriptRows: 12, transcriptPayloadBytes: 512, deliverableRows: 3 };
    else if (path === '/api/admin/db/split-status') json = { alreadyApplied: true, canSplit: false, productCount: 2, splitEnabled: true, controlHasProductTables: false };
    else if (path.endsWith('/csrf')) json = { token: 'fixture-csrf' };
    else if (path.endsWith('/retry')) json = { state: 'retry' };
    else { await route.fulfill({ status: 404, json: {} }); return; }
    await route.fulfill({ json });
  });
  return { writes, reads, setConflict: () => { conflict = true; }, failValidation: (code = 'slack-membership-required') => { validationFailure = code; },
    unavailableAuthorization: () => { authorizationAvailable = false; }, authorizationDecision: (decision: string) => { authorizationDecision = decision; },
    failManager: (reason: string) => { managerFailure = reason; },
    failSlackSetup: (reason: string) => { slackSetupFailure = reason; },
    legacyLinkingHost: () => { slackLinkConfirmationAvailable = false; },
    advanceAuthorizationClock: (ms: number) => { authorizationTimeOffset += ms; },
    setLinks: (value: typeof links) => { links = value; }, setRecentLinkTargets: (value: typeof recentLinkTargets) => { recentLinkTargets = value; },
    deactivateChat: () => { hostReason = 'legacy-engine-active'; },
    startWithoutConnections: () => { connections.splice(0); destinations.splice(0); hostReason = 'legacy-engine-active'; } };
}
const rowButton = (page: Page, title: string) => page.getByRole('button', { name: `Chat for ${title}`, exact: true });

for (const provider of ['slack', 'telegram']) {
  test(`${provider} authorization opens consent and saves verified details without credential entry`, async ({ page, context }) => {
    const mock = await mockApi(page);
    await context.route(/^https:\/\/(slack\.com|t\.me)\//, route => route.fulfill({ contentType: 'text/html', body: '<h1>Provider consent fixture</h1>' }));
    await page.setViewportSize({ width: 390, height: 700 });
    await page.goto('/e2e/chat/settings-fixture.html');
    const providerName = provider === 'slack' ? 'Slack' : 'Telegram';
    await page.getByRole('button', { name: `Add ${providerName} connection`, exact: true }).click();
    await expect(page.getByLabel('Bot token', { exact: true })).toHaveCount(0);
    const consentWindow = page.waitForEvent('popup');
    await page.getByRole('button', { name: `Connect with ${providerName}`, exact: true }).click();
    const popup = await consentWindow;
    await expect(popup.getByRole('heading', { name: 'Provider consent fixture' })).toBeVisible();
    await expect(page.getByText(`Waiting for your approval in ${providerName}`, { exact: true })).toBeVisible();
    if (provider === 'telegram') await expect(page.getByText(/Keep the suggested bot username/)).toBeVisible();
    mock.authorizationDecision('approved');
    const connectionName = page.getByLabel('Connection name', { exact: true });
    await expect(connectionName).toHaveValue(provider === 'slack' ? 'Approved workspace' : '@approved_bot');
    await expect(connectionName).toBeFocused();
    await page.getByRole('checkbox', { name: 'Product P', exact: true }).check();
    await connectionName.fill('Automatic connection');
    await page.getByRole('button', { name: 'Save connection', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Channels · Automatic connection', exact: true })).toBeVisible();
    const saved = mock.writes.find(w => /authorization\/.*\/save$/.test(w.path));
    expect(saved?.body).toEqual({ displayName: 'Automatic connection', productIds: ['p'] });
    expect(mock.writes.some(w => /credentials|\/setup$|\/spawn$/.test(w.path))).toBe(false);
    // Providers may sever window handles with cross-origin isolation. The flow does not depend on automatic closing.
    if (!popup.isClosed()) { expect(await popup.evaluate(() => window.opener)).toBeNull(); await popup.close(); }
  });
}

test('authorization handles denied consent, popup blocking and cancellation without saving', async ({ page }) => {
  const mock = await mockApi(page);
  await page.addInitScript(() => { window.open = () => null; });
  await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'MCP').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('slack');
  await page.getByRole('button', { name: 'Set up connection', exact: true }).click();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Open Slack authorization', exact: true })).toBeVisible();
  mock.authorizationDecision('failed');
  await expect(page.getByRole('alert')).toContainText('declined or cancelled');
  mock.authorizationDecision('waiting');
  await page.getByRole('button', { name: 'Start authorization again', exact: true }).click();
  await expect(page.getByText('Waiting for your approval in Slack', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel connection setup', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true })).toHaveValue('slack');
  expect(mock.writes.filter(w => /\/authorization\/flow-\d$/.test(w.path))).toHaveLength(2);
  expect(mock.writes.some(w => /\/save$|\/credentials\/|\/setup$/.test(w.path))).toBe(false);
});

test('unregistered host offers Telegram setup and keeps provider selection and manual fallback', async ({ page }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization();
  await page.goto('/e2e/chat/settings-fixture.html');
  const telegram = page.getByRole('button', { name: 'Add Telegram connection', exact: true });
  const slack = page.getByRole('button', { name: 'Add Slack connection', exact: true });
  await expect(telegram).toHaveAttribute('aria-pressed', 'false'); await expect(slack).toHaveAttribute('aria-pressed', 'false');
  await telegram.click();
  await expect(page.getByRole('heading', { name: 'Enable Telegram connections', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Open BotFather' })).toHaveAttribute('href', 'https://t.me/BotFather');
  await expect(page.getByRole('button', { name: 'Connect with Telegram', exact: true })).toHaveCount(0);
  await expect(telegram).toHaveAttribute('aria-pressed', 'true'); await expect(slack).toHaveAttribute('aria-pressed', 'false');
  await slack.click();
  await expect(slack).toHaveAttribute('aria-pressed', 'true'); await expect(telegram).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('button', { name: 'Connect with Slack', exact: true })).toBeEnabled();
  await expect(page.getByText(/Forge can create a dedicated Slack app/)).toBeVisible();
  await page.getByRole('button', { name: 'Close connection setup', exact: true }).click();
  await expect(telegram).toHaveAttribute('aria-pressed', 'false'); await expect(slack).toHaveAttribute('aria-pressed', 'false');
  await telegram.click();
  await useManualSetup(page);
  await expect(telegram).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByLabel('Bot token', { exact: true })).toBeVisible();
  expect(mock.writes.filter(w => /authorization|connections/.test(w.path))).toHaveLength(0);
});

test('first Slack setup opens an approval popup and configures the app before review and save', async ({ page, context }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization();
  await page.addInitScript(() => { Object.defineProperty(navigator, 'clipboard', { value: { writeText: async (text: string) => { (window as any).copiedApproval = text; } } }); });
  await context.route(/^https:\/\/app\.slack\.com\/client\//, route => route.fulfill({ contentType: 'text/html', body: '<h1>Slack workspace fixture</h1>' }));
  await page.setViewportSize({ width: 390, height: 740 });
  await page.goto('/e2e/chat/settings-fixture.html');
  await page.getByRole('button', { name: 'Add Slack connection', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Include private channels', exact: true }).check();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Close Slack approval', exact: true })).toBeInViewport();
  const opened = page.waitForEvent('popup');
  await dialog.getByRole('button', { name: 'Copy approval request and open Slack', exact: true }).click();
  const popup = await opened; await expect(popup.getByRole('heading', { name: 'Slack workspace fixture' })).toBeVisible();
  expect(await page.evaluate(() => (window as any).copiedApproval)).toBe('/slackauthticket fixture_approval_request');
  expect(await popup.evaluate(() => window.opener)).toBeNull();
  await expect(dialog.getByText('Approval request copied. Continue in Slack.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Verify and configure automatically', exact: true })).toBeInViewport();
  await dialog.screenshot({ path: '../../../.tmp-build/slack-connect/slack-approval-narrow.png' });
  await dialog.getByLabel('Slack confirmation code', { exact: true }).fill('code-fixture');
  await dialog.getByRole('button', { name: 'Verify and configure automatically', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(page.getByLabel('Connection name', { exact: true })).toHaveValue('Approved workspace');
  await expect(page.getByLabel('Connection name', { exact: true })).toBeFocused();
  await expect(page.getByLabel('Bot token', { exact: true })).toHaveCount(0);
  await page.getByRole('checkbox', { name: 'Product P', exact: true }).check();
  await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Channels · Approved workspace', exact: true })).toBeVisible();
  expect(mock.writes.find(w => w.path.endsWith('/slack/setup'))?.body).toEqual({ providerId: 'slack', includePrivateChannels: true });
  expect(mock.writes.find(w => w.path.endsWith('/slack/approve'))?.body).toEqual({ code: 'code-fixture' });
  expect(mock.writes.find(w => w.path.endsWith('/save'))?.body).toEqual({ displayName: 'Approved workspace', productIds: ['p'] });
  expect(mock.writes.filter(w => w.path.endsWith('/poll'))).toHaveLength(0);
  if (!popup.isClosed()) await popup.close();
});

test('Slack navigation does not wait for stalled clipboard permission and exposes a copy fallback', async ({ page, context }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization();
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => new Promise<void>(resolve => { (window as any).finishClipboard = resolve; }) } });
  });
  await context.route(/^https:\/\/app\.slack\.com\/client\//, route => route.fulfill({ contentType: 'text/html', body: '<h1>Slack workspace fixture</h1>' }));
  await page.goto('/e2e/chat/settings-fixture.html');
  await page.getByRole('button', { name: 'Add Slack connection', exact: true }).click();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  const opened = page.waitForEvent('popup');
  await dialog.getByRole('button', { name: 'Copy approval request and open Slack', exact: true }).click();
  const popup = await opened;
  await expect(popup).toHaveURL('https://app.slack.com/client/', { timeout: 3000 });
  await expect(popup.getByRole('heading', { name: 'Slack workspace fixture' })).toBeVisible();
  expect(await popup.evaluate(() => window.opener)).toBeNull();
  await expect(dialog.getByLabel('Slack approval request', { exact: false })).toHaveValue('/slackauthticket fixture_approval_request');
  await page.evaluate(() => (window as any).finishClipboard());
  await expect(dialog.getByText('Approval request copied. Continue in Slack.', { exact: true })).toHaveCount(0);
  await expect(dialog.getByLabel('Slack approval request', { exact: false })).toBeVisible();
  expect(mock.writes.filter(w => /\/save$|\/slack\/approve$/.test(w.path))).toHaveLength(0);
  await popup.close();
});

test('Slack approval handles blocked popups and clipboard, can reopen, and cancels without saving', async ({ page }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization(); mock.startWithoutConnections();
  await page.addInitScript(() => {
    window.open = () => null;
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: async () => { throw Error('Clipboard blocked'); } } });
  });
  await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'MCP').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('slack');
  await page.getByRole('button', { name: 'Set up connection', exact: true }).click();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  await dialog.getByRole('button', { name: 'Copy approval request and open Slack', exact: true }).click();
  await expect(dialog.getByLabel('Slack approval request', { exact: false })).toHaveValue('/slackauthticket fixture_approval_request');
  await expect(dialog.getByRole('link', { name: 'Open Slack if no window appeared', exact: true })).toHaveAttribute('href', 'https://app.slack.com/client/');
  await dialog.getByRole('button', { name: 'Close Slack approval', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await page.getByRole('button', { name: 'Open Slack approval prompt', exact: true }).click();
  await expect(dialog).toBeVisible(); await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('dialog', { name: 'Remote control · MCP', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel connection setup', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true })).toHaveValue('slack');
  expect(mock.writes.filter(w => /authorization\/flow-\d$/.test(w.path))).toHaveLength(1);
  expect(mock.writes.filter(w => /\/save$|\/slack\/approve$|\/credentials\//.test(w.path))).toHaveLength(0);
});

test('Slack setup reports administrator approval and retains the created app for review', async ({ page }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization(); mock.failSlackSetup('authorization-slack-admin-approval-required');
  await page.goto('/e2e/chat/settings-fixture.html');
  await page.getByRole('button', { name: 'Add Slack connection', exact: true }).click();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  await dialog.getByLabel('Slack confirmation code', { exact: true }).fill('code-fixture');
  await dialog.getByRole('button', { name: 'Verify and configure automatically', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Your Slack workspace requires an administrator');
  await expect(page.getByRole('link', { name: 'Review the created Slack app', exact: true })).toHaveAttribute('href', 'https://api.slack.com/apps/A222');
  await expect(page.getByRole('button', { name: 'Save connection', exact: true })).toHaveCount(0);
  expect(mock.writes.filter(w => w.path.endsWith('/slack/setup'))).toHaveLength(1);
  expect(mock.writes.filter(w => w.path.endsWith('/save'))).toHaveLength(0);
});

test('Slack workspace signup opens directly and resumes with fresh approval after a long signup', async ({ page, context }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization();
  await page.clock.install();
  await context.route(/^https:\/\/slack\.com\/get-started/, route => route.fulfill({ contentType: 'text/html', body: '<h1>Slack signup fixture</h1>' }));
  await page.goto('/e2e/chat/settings-fixture.html');
  await page.getByRole('button', { name: 'Add Slack connection', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Include private channels', exact: true }).check();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  await expect(approval.getByRole('link', { name: 'Find my workspaces', exact: true })).toHaveAttribute('href', 'https://slack.com/signin');
  await approval.getByLabel('Slack confirmation code', { exact: true }).fill('old-unused-code');
  const opened = page.waitForEvent('popup');
  await approval.getByRole('button', { name: 'Create a new Slack workspace', exact: true }).click();
  const popup = await opened;
  await expect(popup).toHaveURL('https://slack.com/get-started#/createnew');
  await expect(popup.getByRole('heading', { name: 'Slack signup fixture' })).toBeVisible();
  expect(await popup.evaluate(() => window.opener)).toBeNull();
  const signup = page.getByRole('dialog', { name: 'Create a Slack workspace', exact: true });
  await expect(signup.getByText('Create and name your workspace', { exact: true })).toBeVisible();
  expect(mock.writes.filter(w => /\/slack\/setup$/.test(w.path))).toHaveLength(1);
  expect(mock.writes.filter(w => /\/save$|\/slack\/approve$/.test(w.path))).toHaveLength(0);
  await page.clock.fastForward(11 * 60 * 1000);
  mock.advanceAuthorizationClock(11 * 60 * 1000);
  await expect(signup.getByRole('button', { name: 'Workspace ready, continue', exact: true })).toBeEnabled();
  await signup.getByRole('button', { name: 'Workspace ready, continue', exact: true }).click();
  await expect(approval).toBeVisible();
  await expect(approval.getByLabel('Slack confirmation code', { exact: true })).toBeEmpty();
  expect(mock.writes.filter(w => /\/slack\/setup$/.test(w.path)).map(w => w.body)).toEqual([
    { providerId: 'slack', includePrivateChannels: true }, { providerId: 'slack', includePrivateChannels: true },
  ]);
  expect(mock.writes.filter(w => w.path === '/api/chat/authorization/flow-0')).toHaveLength(1);
  await approval.getByLabel('Slack confirmation code', { exact: true }).fill('fresh-code-fixture');
  await approval.getByRole('button', { name: 'Verify and configure automatically', exact: true }).click();
  await expect(page.getByLabel('Connection name', { exact: true })).toHaveValue('Approved workspace');
  expect(mock.writes.find(w => w.path.endsWith('/slack/approve'))).toEqual({ path: '/api/chat/authorization/flow-1/slack/approve', body: { code: 'fresh-code-fixture' } });
  expect(mock.writes.filter(w => w.path.endsWith('/save'))).toHaveLength(0);
  await popup.close();
});

test('Slack workspace signup preserves the row draft with blocked popups and can retry failed renewal', async ({ page }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization(); mock.startWithoutConnections();
  await page.addInitScript(() => { window.open = () => null; });
  await page.setViewportSize({ width: 390, height: 740 });
  await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'MCP').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('slack');
  await page.getByRole('button', { name: 'Set up connection', exact: true }).click();
  await page.getByRole('button', { name: 'Connect with Slack', exact: true }).click();
  await page.getByRole('button', { name: 'Create a new Slack workspace', exact: true }).click();
  const signup = page.getByRole('dialog', { name: 'Create a Slack workspace', exact: true });
  await expect(signup.getByRole('link', { name: 'Open Slack signup if no window appeared', exact: true })).toHaveAttribute('href', 'https://slack.com/get-started#/createnew');
  await expect(signup.getByRole('button', { name: 'Close Slack approval', exact: true })).toBeInViewport();
  await expect(signup.getByRole('button', { name: 'Workspace ready, continue', exact: true })).toBeInViewport();
  await signup.screenshot({ path: '../../../.tmp-build/slack-connect/workspace-signup-narrow.png' });
  await page.route('**/api/chat/authorization/slack/setup', route => route.fulfill({ status: 502, json: { title: 'authorization-provider-unavailable' } }));
  await signup.getByRole('button', { name: 'Workspace ready, continue', exact: true }).click();
  await expect(signup.getByRole('alert')).toBeVisible();
  await expect(signup.getByRole('button', { name: 'Workspace ready, continue', exact: true })).toBeEnabled();
  await page.unroute('**/api/chat/authorization/slack/setup');
  await signup.getByRole('button', { name: 'Workspace ready, continue', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Approve Slack setup', exact: true });
  await expect(approval).toBeVisible();
  await expect(approval.getByRole('alert')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Remote control · MCP', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel connection setup', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true })).toHaveValue('slack');
  expect(mock.writes.filter(w => /\/save$|\/slack\/approve$|\/credentials\//.test(w.path))).toHaveLength(0);
});

test('first Telegram setup verifies the manager and continues to consent, then skips setup next time', async ({ page, context }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization();
  await context.route(/^https:\/\/t\.me\//, route => route.fulfill({ contentType: 'text/html', body: '<h1>Telegram consent fixture</h1>' }));
  await page.setViewportSize({ width: 390, height: 700 });
  await page.goto('/e2e/chat/settings-fixture.html');
  await page.getByRole('button', { name: 'Add Telegram connection', exact: true }).click();
  const token = page.getByLabel('Manager bot token', { exact: true });
  await expect(token).toHaveAttribute('type', 'password');
  await expect(page.getByRole('button', { name: 'Verify and continue', exact: true })).toBeDisabled();
  await token.fill('888:MANAGER_FIXTURE_ONLY');
  await page.getByRole('button', { name: 'Verify and continue', exact: true }).scrollIntoViewIfNeeded();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const opening = page.waitForEvent('popup');
  await page.getByRole('button', { name: 'Verify and continue', exact: true }).click();
  const popup = await opening;
  await expect(popup.getByRole('heading', { name: 'Telegram consent fixture' })).toBeVisible();
  await expect(page.getByText('Telegram setup complete · @FixtureManagerBot', { exact: true })).toBeVisible();
  await expect(page.getByText('Waiting for your approval in Telegram', { exact: true })).toBeVisible();
  await expect(token).toHaveCount(0);
  expect(mock.writes.filter(w => w.path.includes('/authorization') && !w.path.endsWith('/poll')).map(w => w.path)).toEqual(['/api/chat/authorization/telegram/manager', '/api/chat/authorization']);
  expect(await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }))).not.toContain('MANAGER_FIXTURE_ONLY');
  mock.authorizationDecision('approved');
  await expect(page.getByLabel('Connection name', { exact: true })).toHaveValue('@approved_bot');
  await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Channels · @approved_bot', exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole('button', { name: 'Add Telegram connection', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Connect with Telegram', exact: true })).toBeEnabled();
  await expect(token).toHaveCount(0);
  if (!popup.isClosed()) await popup.close();
});

test('Telegram setup reports verification failure without starting consent and supports retry in the row drawer', async ({ page }) => {
  const mock = await mockApi(page); mock.unavailableAuthorization(); mock.failManager('authorization-manager-required');
  await page.addInitScript(() => { window.open = () => null; });
  await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'MCP').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
  await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('telegram');
  await page.getByRole('button', { name: 'Set up connection', exact: true }).click();
  const token = page.getByLabel('Manager bot token', { exact: true });
  await token.fill('888:MANAGER_FIXTURE_ONLY');
  await page.getByRole('button', { name: 'Verify and continue', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Bot Management Mode');
  await expect(token).toHaveValue('');
  expect(mock.writes.some(w => w.path === '/api/chat/authorization')).toBe(false);
  mock.failManager(''); await token.fill('888:MANAGER_FIXTURE_ONLY');
  await page.getByRole('button', { name: 'Verify and continue', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Open Telegram authorization', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel connection setup', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Provider', exact: true })).toHaveValue('telegram');
  expect(mock.writes.some(w => /\/save$|\/credentials\/|\/setup$/.test(w.path))).toBe(false);
});

async function useManualSetup(page: Page) {
  await page.getByText('Advanced · use existing credentials', { exact: true }).click();
  await page.getByRole('button', { name: 'Set up manually', exact: true }).click();
}
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

for (const viewport of [{ width: 1280, height: 720 }, { width: 390, height: 844 }]) {
  for (const provider of ['telegram', 'slack']) {
    test(`${provider} setup form is immediately visible and returns to the draft at ${viewport.width}px`, async ({ page }) => {
      const mock = await mockApi(page); mock.startWithoutConnections(); await page.setViewportSize(viewport);
      await page.goto('/e2e/chat/fixture.html'); const row = rowButton(page, '3D-RENDER'); await row.click();
      const enabled = page.getByRole('checkbox', { name: 'Enable chat remote control' }); await enabled.check();
      const providerSelect = page.getByRole('combobox', { name: 'Provider', exact: true }); await providerSelect.selectOption(provider);
      const trigger = page.getByRole('button', { name: 'Set up connection', exact: true }); await trigger.click();
      await useManualSetup(page);
      const name = page.getByRole('textbox', { name: 'Connection name', exact: true });
      // Assert before filling: Playwright's fill would scroll a hidden-below-the-fold editor into view.
      await expect(name).toBeInViewport({ ratio: 1 }); await expect(name).toBeFocused();
      await expect(page.getByRole('heading', { name: 'Set up connection', exact: true })).toBeInViewport();
      await expect(providerSelect).toBeHidden();
      await expect(page.getByRole('button', { name: 'Save connection', exact: true })).toBeInViewport({ ratio: 1 });
      await expect(page.getByRole('button', { name: 'Close remote control', exact: true })).toBeInViewport({ ratio: 1 });
      await name.fill('Unsaved connection');
      await page.getByRole('button', { name: 'Cancel connection setup', exact: true }).click();
      await expect(name).toHaveCount(0); await expect(trigger).toBeEnabled();
      await expect(trigger).toBeInViewport({ ratio: 1 }); await expect(trigger).toBeFocused();
      await expect(providerSelect).toHaveValue(provider); await expect(enabled).toBeChecked();
      await page.keyboard.press('Enter'); await useManualSetup(page); await expect(name).toBeInViewport({ ratio: 1 });
      await expect(name).toBeFocused(); await expect(name).toHaveValue('');
      await page.keyboard.press('Escape'); await expect(row).toBeFocused(); await expect(row).toContainText('Off');
      expect(mock.writes.filter(w => /\/connections\/|\/setup$|\/checks$/.test(w.path))).toHaveLength(0);
    });
  }
}

for (const provider of ['slack', 'telegram']) {
  test(`${provider} walkthrough loads illustrations and keeps setup actions visible while scrolling`, async ({ page }) => {
    const mock = await mockApi(page); mock.startWithoutConnections();
    await page.setViewportSize({ width: 390, height: 620 });
    await page.goto('/e2e/chat/fixture.html'); const row = rowButton(page, '3D-RENDER'); await row.click();
    await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check();
    await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption(provider);
    await page.getByRole('button', { name: 'Set up connection', exact: true }).click();
    await useManualSetup(page);
    await page.getByLabel('Connection name', { exact: true }).fill('Unfinished draft');
    const drawer = page.getByRole('dialog'); const body = drawer.locator('.chat-panel-body');
    await page.getByText(`Step-by-step ${provider === 'slack' ? 'Slack' : 'Telegram'} setup`, { exact: true }).click();
    if (provider === 'slack') {
      const manifest = drawer.getByRole('link', { name: 'Download manifest', exact: true });
      const response = await page.request.get(await manifest.getAttribute('href') as string);
      expect(response.ok()).toBe(true); const yaml = await response.text();
      for (const required of ['chat:write', 'channels:history', 'message.channels', 'socket_mode_enabled: true']) expect(yaml).toContain(required);
      await page.getByText('3. Install the app and copy its bot token', { exact: true }).click();
      const screenshot = drawer.getByAltText(/Slack OAuth and Permissions screen/);
      await screenshot.scrollIntoViewIfNeeded();
      await expect.poll(() => screenshot.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
      await expect(drawer.getByRole('link', { name: /Enlarge screenshot: Copy the highlighted bot token/ })).toBeVisible();
    } else {
      await page.getByText('2. Copy the bot ID into Forge', { exact: true }).click();
      const illustration = drawer.getByAltText(/Illustration: an example token/);
      await illustration.scrollIntoViewIfNeeded();
      await expect.poll(() => illustration.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    }
    await expect(drawer.getByRole('button', { name: 'Save connection', exact: true })).toBeInViewport({ ratio: 1 });
    await expect(drawer.getByRole('button', { name: 'Cancel connection setup', exact: true })).toBeInViewport({ ratio: 1 });
    await expect(drawer.getByRole('button', { name: 'Close remote control', exact: true })).toBeInViewport({ ratio: 1 });
    expect(await body.evaluate(el => getComputedStyle(el).overflowY)).toBe('scroll');
    await page.getByText(`Step-by-step ${provider === 'slack' ? 'Slack' : 'Telegram'} setup`, { exact: true }).click();
    const credentialsSection = drawer.locator('summary').filter({ hasText: /^Credentials$/ });
    await credentialsSection.click();
    await expect(drawer.getByLabel('Bot token', { exact: true })).toBeHidden();
    await credentialsSection.click();
    await expect(drawer.getByLabel('Bot token', { exact: true })).toBeVisible();
    await body.evaluate(el => { el.scrollTop = el.scrollHeight; });
    await expect(drawer.getByRole('checkbox', { name: 'Enable connection', exact: true })).toBeInViewport({ ratio: 1 });
    await expect(drawer.getByRole('button', { name: 'Save connection', exact: true })).toBeInViewport({ ratio: 1 });
    await expect(drawer.getByLabel('Connection name', { exact: true })).toHaveValue('Unfinished draft');
    await drawer.getByRole('button', { name: 'Close remote control', exact: true }).click();
    await expect(drawer).toHaveCount(0); await expect(row).toBeFocused();
    expect(mock.writes.filter(w => /\/connections\/|\/setup$|\/checks$/.test(w.path))).toHaveLength(0);
  });
}

test('inline connection and discovery return to the same row without activating it on cancel', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); await rowButton(page, 'BEYOND-AST').click();
  await page.getByRole('checkbox', { name: 'Enable chat remote control' }).check(); await page.getByRole('combobox', { name: 'Provider', exact: true }).selectOption('telegram');
  const connectionTrigger = page.getByRole('button', { name: 'Set up connection', exact: true }); await connectionTrigger.click();
  await useManualSetup(page);
  await expect(page.getByLabel('Connection name')).toBeInViewport({ ratio: 1 }); await expect(page.getByLabel('Connection name')).toBeFocused();
  await page.getByLabel('Connection name').fill('Second bot'); await page.getByLabel('Bot ID', { exact: true }).fill('22222');
  await page.getByLabel('Bot token', { exact: true }).fill('fixture-secret'); await page.getByRole('checkbox', { name: 'Enable connection', exact: true }).check(); await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Remote control · BEYOND-AST' })).toBeVisible();
  await expect(connectionTrigger).toBeInViewport({ ratio: 1 }); await expect(connectionTrigger).toBeFocused();
  const channelTrigger = page.getByRole('button', { name: 'Discover or add channel', exact: true }); await channelTrigger.click();
  const discover = page.getByRole('button', { name: 'Discover channels', exact: true });
  await expect(discover).toBeInViewport({ ratio: 1 }); await expect(discover).toBeFocused();
  await page.getByRole('button', { name: 'Cancel channel setup', exact: true }).click();
  await expect(channelTrigger).toBeInViewport({ ratio: 1 }); await expect(channelTrigger).toBeFocused();
  await page.keyboard.press('Enter'); await expect(discover).toBeInViewport({ ratio: 1 }); await expect(discover).toBeFocused(); await discover.click();
  await page.getByLabel('Find channel by name').fill('New'); await page.getByRole('button', { name: 'New Ops', exact: true }).click(); await page.getByRole('button', { name: 'Save channel', exact: true }).click();
  await expect(channelTrigger).toBeInViewport({ ratio: 1 }); await expect(channelTrigger).toBeFocused();
  await expect(page.getByRole('combobox', { name: 'Channel', exact: true })).not.toHaveValue(''); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(rowButton(page, 'BEYOND-AST')).toContainText('Off'); expect(mock.writes.filter(w => w.path.endsWith('/setup') || w.path.endsWith('/checks'))).toHaveLength(0);
});

test('Telegram identity requires a personal numeric ID, opens confirmation and detects the verified link', async ({ page, context }) => {
  const mock = await mockApi(page);
  await context.route(/^https:\/\/t\.me\//, route => route.fulfill({ contentType: 'text/html', body: '<h1>Telegram confirmation fixture</h1>' }));
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('telegram');
  await expect(identity.getByLabel('Workspace or bot ID')).toHaveValue('12345');
  await expect(identity.getByLabel('Workspace or bot ID')).not.toBeEditable();
  const sender = identity.getByLabel('My chat user ID', { exact: true });
  const confirm = identity.getByRole('checkbox', { name: 'I confirm this is my own chat account', exact: true });
  const submit = identity.getByRole('button', { name: 'Link my Telegram account', exact: true });
  for (const invalid of ['xd003_forge_chat', '-10012345', '4503599627370496']) {
    await sender.fill(invalid); await confirm.check();
    await expect(sender).toHaveAttribute('aria-invalid', 'true');
    await expect(submit).toBeDisabled();
  }
  expect(mock.writes.filter(w => w.path.endsWith('/link-challenge'))).toHaveLength(0);
  await sender.fill('8458300047');
  await expect(confirm).not.toBeChecked();
  await confirm.check();
  const opened = page.waitForEvent('popup'); await submit.click(); const popup = await opened;
  await expect(popup.getByRole('heading', { name: 'Telegram confirmation fixture' })).toBeVisible();
  await expect(popup).toHaveURL('https://t.me/forge_fixture_bot?start=' + 'F'.repeat(64));
  expect(await popup.evaluate(() => window.opener)).toBeNull();
  expect(mock.writes.find(w => w.path.endsWith('/link-challenge'))?.body).toEqual({ externalTenantId: '12345', externalUserId: '8458300047' });
  await expect(identity.getByRole('link', { name: 'Open Telegram confirmation', exact: true })).toBeVisible();
  await expect(identity).not.toContainText('F'.repeat(64));
  await expect(identity.locator('code')).toHaveCount(0);
  // A different installation's proof must not finish this challenge.
  mock.setLinks([{ providerId: 'telegram', externalTenantId: 'other-bot', externalUserId: '8458300047' }]);
  await identity.getByRole('button', { name: 'Check link status', exact: true }).click();
  await expect(identity).toContainText('other-bot');
  await expect(identity.getByRole('link', { name: 'Open Telegram confirmation', exact: true })).toBeVisible();
  mock.setLinks([{ providerId: 'telegram', externalTenantId: '12345', externalUserId: '8458300047' }]);
  await expect(identity.getByText('Your chat account is linked.', { exact: true })).toBeVisible({ timeout: 8000 });
  await expect(identity.getByRole('link', { name: 'Open Telegram confirmation', exact: true })).toHaveCount(0);
  await expect(submit).toBeDisabled();
  // Provider isolation can sever the window handle; success must not depend on closing it.
  if (!popup.isClosed()) await popup.close();
});

test('Telegram identity offers a direct link when popups are blocked and clears stale confirmation', async ({ page }) => {
  await mockApi(page); await page.addInitScript(() => { window.open = () => null; });
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('telegram');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('8458300047');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account', exact: true }).check();
  await identity.getByRole('button', { name: 'Link my Telegram account', exact: true }).click();
  const fallback = identity.getByRole('link', { name: 'Open Telegram confirmation', exact: true });
  await expect(fallback).toHaveAttribute('href', 'https://t.me/forge_fixture_bot?start=' + 'F'.repeat(64));
  await expect(fallback).toHaveAttribute('rel', 'noopener noreferrer');
  await expect(identity).toContainText('Confirm with Start in Telegram');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('8458300048');
  await expect(fallback).toHaveCount(0);
  await expect(identity.getByRole('button', { name: 'Link my Telegram account', exact: true })).toBeDisabled();
});

test('Telegram identity reports missing confirmation without exposing a raw command', async ({ page }) => {
  await mockApi(page); await page.addInitScript(() => { window.open = () => null; });
  await page.route('**/api/chat/connections/telegram/link-challenge', route => route.fulfill({ json: { token: 'F'.repeat(64), expiresInSeconds: 120, deepLink: null, channelCommand: '/link ' + 'F'.repeat(64) } }));
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('telegram');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('8458300047');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account', exact: true }).check();
  await identity.getByRole('button', { name: 'Link my Telegram account', exact: true }).click();
  await expect(identity.getByRole('alert')).toContainText('Telegram could not open account confirmation');
  await expect(identity).not.toContainText('F'.repeat(64));
  await expect(identity.getByRole('button', { name: 'Check link status', exact: true })).toHaveCount(0);
});

test('Telegram identity fills a previously verified ID and waits for an active receiver', async ({ page }) => {
  const mock = await mockApi(page); mock.deactivateChat();
  mock.setLinks([{ providerId: 'telegram', externalTenantId: 'previous-bot', externalUserId: '8458300047' }]);
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('telegram');
  await expect(identity.getByLabel('My chat user ID', { exact: true })).toHaveValue('8458300047');
  await expect(identity).toContainText('Account linking is waiting for chat activation on this host');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account', exact: true }).check();
  await expect(identity.getByRole('button', { name: 'Link my Telegram account', exact: true })).toBeDisabled();
  expect(mock.writes.filter(w => w.path.endsWith('/link-challenge'))).toHaveLength(0);
});

test('identity autofill does not reuse a Slack ID from another workspace or choose between Telegram accounts', async ({ page }) => {
  const mock = await mockApi(page);
  mock.setLinks([
    { providerId: 'slack', externalTenantId: 'another-workspace', externalUserId: 'U_OTHER' },
    { providerId: 'telegram', externalTenantId: 'first-bot', externalUserId: '1001' },
    { providerId: 'telegram', externalTenantId: 'second-bot', externalUserId: '1002' },
  ]);
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  for (const provider of ['slack', 'telegram']) {
    await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption(provider);
    await expect(identity.getByLabel('My chat user ID', { exact: true })).toBeEmpty();
  }
  expect(mock.writes.filter(w => w.path.endsWith('/link-challenge'))).toHaveLength(0);
});

test('Slack identity waits for the updated backend instead of calling unavailable confirmation endpoints', async ({ page }) => {
  const mock = await mockApi(page); mock.legacyLinkingHost();
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await expect(identity.getByRole('status')).toContainText('updated Forge host');
  await expect(identity.getByRole('button', { name: 'Link my Slack account' })).toBeDisabled();
  expect(mock.reads.filter(p => p.endsWith('/link-destinations'))).toHaveLength(0);
  expect(mock.writes.filter(w => w.path.endsWith('/link-confirmation'))).toHaveLength(0);
});

test('Slack identity opens the selected channel and verifies a private approval without copying commands', async ({ page, context }) => {
  const mock = await mockApi(page);
  await context.route('https://slack.com/app_redirect**', route => route.fulfill({ contentType: 'text/html', body: '<h1>Slack channel fixture</h1>' }));
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await expect(identity.getByLabel('Confirmation channel')).toHaveValue('slack-render');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('U1');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check();
  const opened = page.waitForEvent('popup'); await identity.getByRole('button', { name: 'Link my Slack account' }).click();
  const popup = await opened; await expect(popup).toHaveURL('https://slack.com/app_redirect?team=T1&channel=C1');
  const dialog = page.getByRole('dialog', { name: 'Confirm your Slack account' });
  await expect(dialog.getByRole('status')).toHaveText('Slack accepted the request. Waiting for your approval.');
  expect(mock.writes.find(w => w.path.endsWith('/link-confirmation'))?.body).toEqual({ destinationId: 'slack-render', externalUserId: 'U1' });
  expect(mock.writes.filter(w => w.path.endsWith('/link-challenge'))).toHaveLength(0);
  await expect(page.locator('code').filter({ hasText: '!forge link' })).toHaveCount(0);
  await page.screenshot({ path: '../../../.tmp-build/chat-identity/slack-identity-desktop.png' });
  mock.setLinks([{ providerId: 'slack', externalTenantId: 'TOTHER', externalUserId: 'U1' }]);
  await page.waitForTimeout(2800); await expect(dialog).toBeVisible();
  mock.setLinks([{ providerId: 'slack', externalTenantId: 'T1', externalUserId: 'U1' }]);
  await expect(dialog).toHaveCount(0); await expect(identity.getByRole('status')).toContainText(['already linked', 'Your chat account is linked.']);
});

test('Slack identity popup fallback, expiry and cancel remain usable on a small screen', async ({ page }) => {
  const mock = await mockApi(page); await page.clock.install();
  await page.addInitScript(() => { window.open = () => null; });
  await page.setViewportSize({ width: 390, height: 620 }); await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('U1');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check();
  await identity.getByRole('button', { name: 'Link my Slack account' }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirm your Slack account' });
  await expect(dialog.getByRole('link')).toHaveAttribute('href', 'https://slack.com/app_redirect?team=T1&channel=C1');
  await expect(dialog.getByRole('button', { name: 'Close account confirmation' })).toBeInViewport();
  await expect(dialog.getByRole('button', { name: 'Send a new confirmation' })).toBeInViewport();
  expect(await dialog.evaluate(d => d.scrollWidth <= d.clientWidth)).toBe(true);
  await page.clock.fastForward(121000); await expect(dialog.getByRole('status')).toContainText('Confirmation expired');
  await dialog.getByRole('button', { name: 'Send a new confirmation' }).click();
  await expect(dialog.getByRole('status')).toContainText('Waiting for your approval');
  expect(mock.writes.filter(w => w.path.endsWith('/link-confirmation') && w.body)).toHaveLength(2);
  await page.screenshot({ path: '../../../.tmp-build/chat-identity/slack-identity-mobile.png' });
  await dialog.getByRole('button', { name: 'Cancel linking' }).click(); await expect(dialog).toHaveCount(0);
  expect(mock.writes.filter(w => w.path.endsWith('/link-confirmation') && !w.body)).toHaveLength(1);
  await expect(identity.getByRole('button', { name: 'Link my Slack account' })).toBeDisabled();
});

test('Slack identity reports missing channel membership without reverting to a command', async ({ page }) => {
  await mockApi(page); await page.addInitScript(() => { window.open = () => null; });
  await page.route('**/link-confirmation', route => route.request().method() === 'POST'
    ? route.fulfill({ status: 409, json: { title: 'slack-link-user-not-in-channel' } }) : route.fallback());
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await identity.getByLabel('My chat user ID', { exact: true }).fill('U1');
  await identity.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check();
  await identity.getByRole('button', { name: 'Link my Slack account' }).click();
  const dialog = page.getByRole('dialog', { name: 'Confirm your Slack account' });
  await expect(dialog.getByRole('alert')).toContainText('Join the selected Slack channel');
  await expect(dialog.getByRole('button', { name: 'Send a new confirmation' })).toBeEnabled();
  await expect(page.locator('code').filter({ hasText: '!forge link' })).toHaveCount(0);
});

test('settings repair, independent verification, explicit test send, host alerts and links', async ({ page }) => {
  const mock = await mockApi(page); await page.goto('/e2e/chat/fixture.html'); await page.getByRole('button', { name: 'Chat settings', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Host alerts', exact: true })).toBeVisible(); await expect(page.getByText('Orphaned scopes 2')).toBeVisible();
  const library = page.getByRole('region', { name: 'Saved connections', exact: true });
  const workspace = page.getByRole('region', { name: 'Connection workspace', exact: true });
  await expect(library.getByRole('button', { name: 'Repair / edit' })).toHaveCount(2);
  await expect(page.getByRole('combobox', { name: 'Host provider', exact: true })).toBeHidden();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    const saved = (await library.boundingBox())!; const setup = (await workspace.boundingBox())!;
    if (width === 1440) { expect(Math.abs(saved.y - setup.y)).toBeLessThan(1); expect(setup.x).toBeGreaterThan(saved.x + saved.width); }
    else { expect(setup.y).toBeGreaterThan(saved.y + saved.height); expect(setup.x + setup.width).toBeLessThanOrEqual(width); }
    await expect(page.getByRole('button', { name: 'Add Slack connection', exact: true })).toBeVisible();
    await page.screenshot({ path: `../../../.tmp-build/chat-stage5/settings-layout-${width}.png`, fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.getByRole('button', { name: 'Render workspace slack · configured' }).click();
  await expect(page.getByText('Inbound reply', { exact: true })).toBeVisible(); expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(0);
  mock.failValidation(); await page.getByRole('button', { name: 'Validate channel access' }).click(); await expect(page.getByRole('alert')).toContainText('Invite the Slack app');
  expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(0);
  await page.getByRole('button', { name: 'Send test message' }).click(); expect(mock.writes.filter(w => w.path.endsWith('/checks'))).toHaveLength(1);
  await page.getByRole('button', { name: 'Repair / edit' }).nth(1).click(); await expect(page.getByLabel('Workspace ID')).toHaveValue('T1'); await page.getByLabel('Bot token', { exact: true }).fill('replacement-fixture'); await page.getByRole('button', { name: 'Save connection', exact: true }).click();
  await page.getByText('Configure host alerts', { exact: true }).click();
  await page.getByRole('combobox', { name: 'Host provider', exact: true }).selectOption('slack'); await page.getByRole('combobox', { name: 'Host channel', exact: true }).selectOption('slack-render'); await page.getByRole('button', { name: 'Save host alerts' }).click();
  expect(mock.writes.find(w => w.path.startsWith('/api/chat/host/subscriptions/'))?.body.eventKinds).toEqual(['queue-strain', 'human-interaction']);
  await page.getByText('Link or re-link my account', { exact: true }).click(); await page.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack'); await expect(page.getByLabel('Workspace or bot ID')).toHaveValue('T1'); await page.getByLabel('My chat user ID').fill('U1');
  await page.evaluate(() => { window.open = () => null; });
  await expect(page.getByRole('button', { name: 'Link my Slack account' })).toBeDisabled(); await page.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check(); await page.getByRole('button', { name: 'Link my Slack account' }).click();
  await expect(page.getByRole('dialog', { name: 'Confirm your Slack account' })).toBeVisible(); await page.getByRole('button', { name: 'Cancel linking' }).click();
  await page.getByLabel('My chat user ID').fill('U2'); await expect(page.getByRole('button', { name: 'Link my Slack account' })).toBeDisabled();
  await page.getByLabel('My chat user ID').fill('U1'); await page.getByRole('checkbox', { name: 'I confirm this is my own chat account' }).check(); await page.getByRole('button', { name: 'Link my Slack account' }).click();
  mock.setLinks([{ providerId: 'slack', externalTenantId: 'T1', externalUserId: 'U1' }]);
  await expect(page.getByRole('dialog', { name: 'Confirm your Slack account' })).toHaveCount(0); await page.getByText('Verified account details · 1', { exact: true }).click();
  await expect(page.getByText('slack · T1 · U1 — linked')).toBeVisible(); await page.getByRole('button', { name: 'Revoke my chat links' }).click(); await expect(page.getByText('Not linked', { exact: true })).toBeVisible();
  await page.screenshot({ path: '../../../.tmp-build/chat-stage5/chat-settings.png', fullPage: true });
});

test('Slack restores a previous member ID after reload without claiming proof or checking consent', async ({ page }) => {
  const mock = await mockApi(page);
  mock.setRecentLinkTargets([{ connectionId: 'slack', externalTenantId: 'T1', externalUserId: 'UPREVIOUS' }]);
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  for (let attempt = 0; attempt < 2; attempt++) {
    await identity.getByText('Link or re-link my account', { exact: true }).click();
    await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
    await expect(identity.getByLabel('My chat user ID', { exact: true })).toHaveValue('UPREVIOUS');
    await expect(identity).toContainText('Filled from your previous attempt');
    await expect(identity.getByTitle('Identity not linked to this installation')).toHaveCount(2);
    const ownership = identity.getByRole('checkbox', { name: 'I confirm this is my own chat account' });
    await expect(ownership).not.toBeChecked();
    const link = identity.getByRole('button', { name: 'Link my Slack account' });
    await expect(link).toBeDisabled(); await ownership.check(); await expect(link).toBeEnabled();
    await identity.getByLabel('My chat user ID', { exact: true }).fill('UCORRECTED');
    await expect(ownership).not.toBeChecked(); await expect(link).toBeDisabled();
    await expect(identity).not.toContainText('Filled from your previous attempt');
    if (!attempt) await page.reload();
  }
  expect(mock.writes.filter(w => w.path.endsWith('/link-confirmation') || w.path.endsWith('/link-challenge'))).toHaveLength(0);
});

test('Slack input recovery respects connection and workspace and prefers an already verified account', async ({ page }) => {
  const mock = await mockApi(page);
  mock.setRecentLinkTargets([
    { connectionId: 'slack', externalTenantId: 'TOTHER', externalUserId: 'UOTHERWORKSPACE' },
    { connectionId: 'another-connection', externalTenantId: 'T1', externalUserId: 'UOTHERCONNECTION' },
  ]);
  await page.goto('/e2e/chat/settings-fixture.html');
  const identity = page.getByRole('region', { name: 'Operator identity', exact: true });
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await expect(identity.getByLabel('My chat user ID', { exact: true })).toBeEmpty();
  mock.setLinks([{ providerId: 'slack', externalTenantId: 'T1', externalUserId: 'UVERIFIED' }]);
  mock.setRecentLinkTargets([{ connectionId: 'slack', externalTenantId: 'T1', externalUserId: 'UOLD' }]);
  await page.reload();
  await identity.getByText('Link or re-link my account', { exact: true }).click();
  await identity.getByRole('combobox', { name: 'Identity connection', exact: true }).selectOption('slack');
  await expect(identity.getByLabel('My chat user ID', { exact: true })).toHaveValue('UVERIFIED');
  await expect(identity).not.toContainText('Filled from your previous attempt');
  await expect(identity.getByRole('status')).toContainText('already linked');
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
}for (const width of [1440, 390]) {
  test(`full Settings page tabs preserve drafts and keep Close and bottom content reachable at ${width}px`, async ({ page }) => {
    const mock = await mockApi(page); mock.startWithoutConnections();
    const errors: string[] = []; page.on('pageerror', error => errors.push(error.message));
    await page.setViewportSize({ width, height: 620 });
    await page.goto('/e2e/chat/settings-fixture.html');
    const close = page.getByRole('button', { name: 'Close settings', exact: true });
    const scroll = page.getByRole('region', { name: 'Settings sections', exact: true });
    const chat = page.getByRole('tab', { name: 'Chat', exact: true });
    const repositories = page.getByRole('tab', { name: 'Repository providers', exact: true });
    const general = page.getByRole('tab', { name: 'General', exact: true });
    await expect(close).toBeInViewport({ ratio: 1 });
    await expect(chat).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('#settings-panel-repositories')).toBeHidden();
    await page.getByRole('button', { name: 'Add Slack connection', exact: true }).click();
    await useManualSetup(page);
    await page.getByLabel('Connection name', { exact: true }).fill('Keep this chat draft');
    await page.getByText('Step-by-step Slack setup', { exact: true }).click();
    await page.getByText('3. Install the app and copy its bot token', { exact: true }).click();
    const image = page.getByAltText(/Slack OAuth and Permissions screen/);
    await image.scrollIntoViewIfNeeded();
    await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    await expect(close).toBeInViewport({ ratio: 1 });
    await page.getByRole('button', { name: 'Save connection', exact: true }).scrollIntoViewIfNeeded();
    await expect(page.getByRole('button', { name: 'Save connection', exact: true })).toBeInViewport({ ratio: 1 });
    await repositories.click();
    await expect(page.locator('#settings-panel-chat')).toBeHidden();
    await expect.poll(() => scroll.evaluate(el => el.scrollTop)).toBe(0);
    const owner = page.getByPlaceholder('microsoft', { exact: true });
    await owner.fill('keep-repository-draft');
    await chat.click();
    await expect(page.getByLabel('Connection name', { exact: true })).toHaveValue('Keep this chat draft');
    await repositories.click();
    await expect(owner).toHaveValue('keep-repository-draft');
    await repositories.focus(); await page.keyboard.press('ArrowRight');
    await expect(general).toBeFocused(); await expect(general).toHaveAttribute('aria-selected', 'true');
    const maintenance = page.locator('#settings-maintenance > summary');
    await maintenance.click();
    await scroll.evaluate(el => { el.scrollTop = el.scrollHeight; });
    await expect(close).toBeInViewport({ ratio: 1 });
    const bounds = await scroll.evaluate(el => {
      const view = el.closest('#view-settings')!;
      const panel = el.querySelector('#settings-maintenance')!;
      return { bottom: panel.getBoundingClientRect().bottom, viewportBottom: el.getBoundingClientRect().bottom,
        overflow: getComputedStyle(el).overflowY, clippedWidth: el.scrollWidth - el.clientWidth, outerScroll: view.scrollHeight - view.clientHeight };
    });
    expect(bounds.bottom).toBeLessThanOrEqual(bounds.viewportBottom);
    expect(bounds.overflow).toBe('scroll'); expect(bounds.clippedWidth).toBeLessThanOrEqual(1); expect(bounds.outerScroll).toBeLessThanOrEqual(1);
    await maintenance.click(); await expect(page.getByText('Per-product database split', { exact: true })).toBeHidden();
    await repositories.click(); await page.reload();
    await expect(repositories).toHaveAttribute('aria-selected', 'true');
    await expect(close).toBeInViewport({ ratio: 1 }); await close.click();
    await expect(page.getByRole('heading', { name: 'Workstreams fixture' })).toBeVisible();
    await page.getByRole('link', { name: 'Open Settings', exact: true }).click();
    await general.click(); await close.click();
    await expect(page.getByRole('heading', { name: 'Workstreams fixture' })).toBeVisible();
    expect(mock.writes.filter(write => write.path !== '/api/chat/preview')).toHaveLength(0);
    expect(errors).toHaveLength(0);
  });
}