// -----------------------------------------------------------------------
// docs/forge/e2e.md Stage 8.3 — Playwright smoke for the legacy `qa`
// phase badge on the /workstreams index. Mirrors the Stage 8.1 / 8.2
// pattern: mock `/api/stories/spawns` (+ per-spawn `/spawn`) via
// `page.route` with a fixture that includes ONE legacy spawn at
// `phase==='qa'`. Navigate to /workstreams; assert the row's badge
// carries the `phase-badge--legacy` modifier + the operator-facing
// drain tooltip from `LEGACY_PHASE_TOOLTIP.qa`.
// -----------------------------------------------------------------------

import { expect, test, type Page } from '@playwright/test';

const API = 'http://localhost:5180';
const LEGACY_STORY = 'stage83-legacy-qa';
const NEW_STORY = 'stage83-new-rotation';

// Fixture: two spawns. The first is a legacy spawn parked at
// `phase==='qa'` (Stage 8.3 must render its badge muted with
// tooltip). The second is a new-rotation spawn at `phase='implement'`
// — anti-regression that canonical phases keep rendering with the
// original saturated palette + no tooltip.
const SPAWNS_FIXTURE = [
  {
    id: 'spawn-' + LEGACY_STORY,
    storyId: LEGACY_STORY,
    storyTitle: 'Legacy QA-parked spawn',
    repoId: 'repo-stage83',
    phase: 'qa',
    storyDescription: '',
    storyPoints: 3,
    baseBranch: 'main',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  },
  {
    id: 'spawn-' + NEW_STORY,
    storyId: NEW_STORY,
    storyTitle: 'New rotation spawn',
    repoId: 'repo-stage83',
    phase: 'implement',
    storyDescription: '',
    storyPoints: 3,
    baseBranch: 'main',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  },
];

const EMPTY_DETAIL = (storyId: string, phase: string) => ({
  spawn: {
    id: 'spawn-' + storyId,
    storyId,
    storyTitle: 'Stage 8.3 fixture',
    repoId: 'repo-stage83',
    phase,
    decompositionApproved: false,
    storyDescription: '',
    storyPoints: 3,
    baseBranch: 'main',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  },
  qaPrMerged: false,
  workstreams: [],
});

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

async function installFixtureRoutes(page: Page) {
  // The /workstreams index calls `forgeApi.listStorySpawns()` →
  // GET /api/stories/spawns AND per-spawn GET /api/stories/{id}/spawn.
  await page.route('**/api/stories/spawns**', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(SPAWNS_FIXTURE),
    });
  });
  await page.route(`**/api/stories/${LEGACY_STORY}/spawn`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(EMPTY_DETAIL(LEGACY_STORY, 'qa')),
    });
  });
  await page.route(`**/api/stories/${NEW_STORY}/spawn`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(EMPTY_DETAIL(NEW_STORY, 'implement')),
    });
  });
  // Per-spawn artifacts endpoint — empty payload keeps the index from
  // erroring; never asserted by this spec.
  await page.route(/\/api\/stories\/[^/]+\/spawn\/artifacts$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        plan: { branch: null, pr: null, mergedAt: null, mergeSha: null },
        qa: { branch: null, pr: null, mergedAt: null, mergeSha: null },
        workstreams: [],
      }),
    });
  });
}

// ── 1. Legacy `qa` spawn renders muted badge with drain tooltip ──────

test('legacy `qa` spawn row carries the muted badge + drain tooltip', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto('/workstreams');

  // Wait for the spawn list to settle. The row container has the
  // `story-row phase-{phase}` shape; locate by phase class.
  const legacyRow = page.locator('.swarm-ctrl .story-row.phase-qa').first();
  await expect(legacyRow).toBeVisible();
  await expect(legacyRow).toContainText('Legacy QA-parked spawn');

  // The badge inside the legacy row carries BOTH the legacy modifier
  // AND `data-phase-legacy="true"` — the latter is a stable hook so
  // a future CSS overhaul doesn't break the test.
  const badge = legacyRow.locator('.phase-badge');
  await expect(badge).toBeVisible();
  await expect(badge).toHaveClass(/phase-badge--legacy/);
  await expect(badge).toHaveAttribute('data-phase-legacy', 'true');

  // Tooltip is the operator-facing drain explanation. We assert two
  // pinned substrings so a future tooltip rewrite stays informative
  // without breaking the test on every word change.
  const title = await badge.getAttribute('title');
  expect(title).not.toBeNull();
  expect(title!).toMatch(/legacy/i);
  expect(title!).toMatch(/per-stage workstreams|Stage 5|drain/i);
});

// ── 2. New-rotation spawn renders a non-legacy badge (anti-regression) ──

test('new-rotation spawn (phase=implement) renders the badge WITHOUT the legacy modifier', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto('/workstreams');

  const newRow = page.locator('.swarm-ctrl .story-row.phase-implement').first();
  await expect(newRow).toBeVisible();
  await expect(newRow).toContainText('New rotation spawn');

  const badge = newRow.locator('.phase-badge');
  await expect(badge).toBeVisible();
  // The badge has the phase class but NOT the legacy modifier.
  await expect(badge).toHaveClass(/\bimplement\b/);
  await expect(badge).not.toHaveClass(/phase-badge--legacy/);
  // `data-phase-legacy` is unset (the JSX uses `|| undefined` to
  // avoid emitting the attribute at all for canonical phases).
  await expect(badge).not.toHaveAttribute('data-phase-legacy', 'true');
  // No tooltip on a canonical-phase badge.
  expect(await badge.getAttribute('title')).toBeNull();
});
