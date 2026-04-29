// -----------------------------------------------------------------------
// Phase 18.5 + 18.6 — Playwright smoke tests against the live dev UI.
//
// The pure-logic contracts (pill colour palette, "Lead 24×7" predicate,
// `isOnShiftLocal` truth tables, TS type union) are already locked in by
// `src/views/shifts.spec.ts`. This Playwright run is the visual close-
// the-loop: the pill actually appears in the rendered DOM, the lead row
// actually shows the green dot, and the inline shift toggle actually
// patches the crew end-to-end.
//
// Identity: the React app reads `/api/me`, which resolves via the
// connected ADO/GitHub provider — the X-Forge-User header doesn't apply.
// `globalSetup.ts` snapshots and promotes every user to product-owner;
// `globalTeardown.ts` restores them. The non-PO test demotes the active
// user mid-flight and restores it before exit.
// -----------------------------------------------------------------------

import { expect, test } from '@playwright/test';
import Database from 'better-sqlite3';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';

const API = 'http://localhost:5180';
const DAY_CREW_ID = 'crew-eng';

// ── DB helpers (mirror globalSetup) ──────────────────────────────────

const DB_CANDIDATES = [
  resolve(process.cwd(), '../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../../forge/Forge.Api/forge.db'),
  resolve(process.cwd(), 'src/forge/Forge.Api/forge.db'),
];

function openDb(): Database.Database {
  const dbPath = DB_CANDIDATES.find((p) => existsSync(p));
  if (!dbPath) throw new Error(`forge.db not found in any of: ${DB_CANDIDATES.join(', ')}`);
  return new Database(dbPath);
}

async function getActiveUserEmail(request: import('@playwright/test').APIRequestContext): Promise<string> {
  const me = await request.get(`${API}/api/me`);
  expect(me.ok()).toBe(true);
  const body = await me.json();
  return body.email as string;
}

// ── Page helpers ─────────────────────────────────────────────────────

async function selectEngineering(page: import('@playwright/test').Page): Promise<void> {
  await page.locator('aside').getByRole('button', { name: /Engineering/i }).first().click();
}

async function gotoPerformanceTab(page: import('@playwright/test').Page): Promise<void> {
  await page.getByRole('button', { name: /^performance$/i }).click();
}

// ── Fixtures ─────────────────────────────────────────────────────────

test.beforeAll(async ({ request }) => {
  const me = await request.get(`${API}/api/me`).catch(() => null);
  if (!me?.ok()) {
    throw new Error(
      `Forge.Api not reachable at ${API}. ` +
      'Start it with `dotnet run --project src/forge/Forge.Api/Forge.Api.csproj` and rerun.',
    );
  }
});

// Reset the day crew's shift to 'day' before each test so the toggle
// scenario has a known starting point even if the previous run flipped it.
test.beforeEach(async ({ request }) => {
  await request.patch(`${API}/api/crews/${DAY_CREW_ID}`, {
    headers: { 'X-Forge-User': 'e2e@forge.local', 'Content-Type': 'application/json' },
    data: { shift: 'day' },
  });
});

// ── 18.5 — pill renders + colour matches the band ──────────────────────

test('day-shift crew renders the amber Day pill', async ({ page }) => {
  await page.goto('/crews');
  await selectEngineering(page);
  await gotoPerformanceTab(page);

  const pill = page.getByTestId('shift-pill');
  await expect(pill).toBeVisible();
  await expect(pill).toHaveAttribute('data-shift', 'day');
  await expect(pill).toHaveText('Day');
  await expect(pill).toHaveCSS('color', 'rgb(255, 196, 90)');
});

test('night-shift crew renders the lavender Night pill', async ({ page }) => {
  await page.goto('/crews');
  await page.locator('aside [role="button"][title*="shift"]').first().click();
  await selectEngineering(page);
  await gotoPerformanceTab(page);

  const pill = page.getByTestId('shift-pill');
  await expect(pill).toBeVisible();
  await expect(pill).toHaveAttribute('data-shift', 'night');
  await expect(pill).toHaveText('Night');
  await expect(pill).toHaveCSS('color', 'rgb(178, 165, 255)');
});

// ── 18.5 — PO toggling the pill writes the patch and flips the colour ──

test('PO toggling the day pill flips it to Night without a page refresh', async ({ page, request }) => {
  await page.goto('/crews');
  await selectEngineering(page);
  await gotoPerformanceTab(page);

  const pill = page.getByTestId('shift-pill');
  await expect(pill).toHaveAttribute('data-shift', 'day');
  await expect(pill).toHaveAttribute('data-editable', 'true');

  await pill.click();
  await expect(pill).toHaveAttribute('data-shift', 'night');
  await expect(pill).toHaveText('Night');
  await expect(pill).toHaveCSS('color', 'rgb(178, 165, 255)');

  // The persisted side: GET /api/crews/{id} agrees with the visual.
  const after = await request.get(`${API}/api/crews/${DAY_CREW_ID}`, {
    headers: { 'X-Forge-User': 'e2e@forge.local' },
  });
  const body = await after.json();
  expect(body.shift).toBe('night');
});

// ── 18.5 — non-PO sees pill as read-only (static span, not button) ─────

test('non-PO sees the shift pill as a static (non-button) span', async ({ page, request }) => {
  // The active user was promoted to PO in globalSetup. Demote them just
  // for this test, then restore. We snapshot the role in-test (rather
  // than relying on globalSetup's snapshot file) so this test is self-
  // contained even if it's run in isolation.
  const email = await getActiveUserEmail(request);

  const db = openDb();
  const original = (db.prepare('SELECT Role FROM Users WHERE Email = ?').get(email) as { Role: string }).Role;
  db.prepare('UPDATE Users SET Role = ? WHERE Email = ?').run('human', email);
  db.close();

  try {
    await page.goto('/crews');
    await selectEngineering(page);
    await gotoPerformanceTab(page);

    const pill = page.getByTestId('shift-pill');
    await expect(pill).toBeVisible();
    // For a non-PO, the pill renders as a static <span data-editable="false">,
    // not a <button> — so the click handler never fires.
    const tagName = await pill.evaluate((el) => el.tagName.toLowerCase());
    expect(tagName).toBe('span');
    await expect(pill).toHaveAttribute('data-editable', 'false');
  } finally {
    const restore = openDb();
    restore.prepare('UPDATE Users SET Role = ? WHERE Email = ?').run(original, email);
    restore.close();
  }
});

// ── 18.6 — visual lead-row green dot + "Lead 24×7" pill ────────────────

test('CrewsView Roster lead row shows the permanent green dot', async ({ page }) => {
  await page.goto('/crews');
  await selectEngineering(page);
  await page.getByRole('button', { name: /^roster$/i }).click();

  const dot = page.getByTestId('lead-green-dot');
  await expect(dot).toBeVisible();
  // Match any green-dominant rgb() — the success token resolves through
  // CSS variables and themes serve different shades; what matters here
  // is "the dot exists and is rendered green-ish", not the exact hue.
  await expect(dot).toHaveCSS('background-color', /^rgb\(\s*\d+\s*,\s*[12]\d{2}\s*,\s*\d+\s*\)$/);
});

test('"Lead 24×7" pill renders on a crew with a lead and schedule enabled', async ({ page }) => {
  await page.goto('/crews');
  await selectEngineering(page);
  await gotoPerformanceTab(page);

  const pill = page.getByTestId('lead-24-pill');
  await expect(pill).toBeVisible();
  await expect(pill).toHaveText(/Lead\s*24×7/i);
});
