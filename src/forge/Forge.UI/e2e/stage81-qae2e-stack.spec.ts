// -----------------------------------------------------------------------
// docs/forge/e2e.md Stage 8.1 — Playwright smoke for the stacked
// impl/qa-e2e row contract in `SwarmMasterPanel`.
//
// Strategy: instead of seeding a real spawn through the orchestrator
// (which would require running the whole gen↔eval loop), intercept the
// two endpoints `SwarmStoryView` calls on mount —
// `/api/stories/{storyId}/spawn` + `/api/stories/{storyId}/spawn/artifacts`
// — with a hand-rolled fixture that declares 3 impl stages, 2 of which
// carry a qa-e2e companion. Forge.Api must still be reachable for the
// app's `/api/me` auth check (matching the phase18 spec convention);
// the per-story endpoints are the only ones the test mocks.
// -----------------------------------------------------------------------

import { expect, test, type Page } from '@playwright/test';

const API = 'http://localhost:5180';
const STORY_ID = 'stage81-qae2e-fixture';

// The fixture: 3 impl stages on a single phase; stages A + C have a
// qa-e2e sibling, stage B doesn't. Combined ≡ 5 workstream rows in the
// payload; the selector should pair A→qaA + C→qaC and leave B
// standalone, so the phase-board renders 3 columns + 5 boxes.
const SPAWN_DETAIL_FIXTURE = {
  spawn: {
    id: 'spawn-' + STORY_ID,
    storyId: STORY_ID,
    storyTitle: 'Stage 8.1 fixture',
    repoId: 'repo-stage81',
    phase: 'implement',
    planBranch: null,
    planPrId: null,
    decompositionJson: null,
    decompositionApproved: true,
    qaBranch: null,
    qaPrId: null,
    storyDescription: '',
    storyPoints: 3,
    baseBranch: 'main',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  },
  qaPrMerged: false,
  workstreams: [
    implRow('ws-stage81-a', 'A — alpha', 'phase-foundation/stage-a', 0),
    qaE2eRow('ws-stage81-a-e2e', 'A — E2E', 'phase-foundation/stage-a/e2e', 1),
    implRow('ws-stage81-b', 'B — beta', 'phase-foundation/stage-b', 2),
    implRow('ws-stage81-c', 'C — gamma', 'phase-ingest/stage-c', 3),
    qaE2eRow('ws-stage81-c-e2e', 'C — E2E', 'phase-ingest/stage-c/e2e', 4),
  ],
};

function implRow(id: string, name: string, planAnchor: string, sort: number) {
  return {
    id,
    name,
    branch: `ws/${id}`,
    status: 'active',
    cards: [],
    prs: [],
    deps: [],
    phaseAnchor: planAnchor.split('/')[0],
    planAnchor,
    category: 'impl',
    agents: [],
    iter: 0,
    latestScore: null,
    latestVerdict: null,
    latestGenAgentId: null,
    latestEvalAgentId: null,
    lastActiveAt: null,
    costUsd: 0,
    commits: 0,
    ahead: 0,
    behind: 0,
    unansweredQuestionCount: 0,
    stallReason: null,
    worktreeFolder: null,
    stepDefPrId: null,
    stepDefBranch: null,
    stepDefMerged: false,
    sort,
  };
}

function qaE2eRow(id: string, name: string, planAnchor: string, sort: number) {
  return { ...implRow(id, name, planAnchor, sort), category: 'qa-e2e' };
}

const ARTIFACTS_FIXTURE = {
  plan: { branch: null, pr: null, mergedAt: null, mergeSha: null },
  qa: { branch: null, pr: null, mergedAt: null, mergeSha: null },
  workstreams: [],
};

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
  await page.route(`**/api/stories/${STORY_ID}/spawn`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(SPAWN_DETAIL_FIXTURE),
    });
  });
  await page.route(`**/api/stories/${STORY_ID}/spawn/artifacts`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(ARTIFACTS_FIXTURE),
    });
  });
  // The DocAgentAvatar component polls `/spawn/artifacts/{key}/iterations`
  // — return empty iterations so the panel renders without console noise.
  await page.route(/\/api\/stories\/[^/]+\/spawn\/artifacts\/.+\/iterations$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        iterations: [],
        totalCostUsd: 0,
        status: 'pending',
        stallReason: null,
        unansweredQuestions: 0,
        generator: null,
        evaluator: null,
      }),
    });
  });
}

// ── 1. [ui-e2e] 3 stages (2 with e2e, 1 without) renders 5 boxes in 3 columns ──

test('stacked phase-board renders 5 boxes across 3 stage columns', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto(`/workstreams/${STORY_ID}?mode=impl&master=open`);

  // Wait for the phase-board to mount and the first impl card to render.
  const board = page.locator('.swarm-ctrl .phase-board');
  await expect(board).toBeVisible();
  await expect(board.locator('.phase').first()).toBeVisible();

  // 5 total `.phase` elements (3 impl + 2 qa-e2e).
  const allPhases = board.locator('.phase');
  await expect(allPhases).toHaveCount(5);

  // 3 "stage columns" — top-level children of `.phase-board`. A column
  // is either a `.phase-stack` (2 boxes inside) or a standalone
  // `.phase` (1 box).
  const columns = board.locator(':scope > .phase-stack, :scope > .phase');
  await expect(columns).toHaveCount(3);

  // The 2 stacked columns each contain exactly 2 `.phase` boxes
  // (impl on top + qa-e2e beneath); the 1 lone-impl column has 1.
  const stacks = board.locator(':scope > .phase-stack');
  await expect(stacks).toHaveCount(2);
  for (let i = 0; i < 2; i++) {
    const phasesInStack = stacks.nth(i).locator('> .phase');
    await expect(phasesInStack).toHaveCount(2);
    // First child is the impl row (no `phase--qae2e`), second is qa-e2e.
    await expect(phasesInStack.nth(0)).not.toHaveClass(/phase--qae2e/);
    await expect(phasesInStack.nth(1)).toHaveClass(/phase--qae2e/);
  }

  // The 3rd column is the lone impl (stage B with no qa-e2e sibling).
  const loneImpl = board.locator(':scope > .phase').first();
  await expect(loneImpl).toBeVisible();
  await expect(loneImpl).toHaveAttribute('data-sel-key', 'phase:ws-stage81-b');
});

// ── 2. [ui-e2e] qa-e2e box renders with the green team accent ────────

test('qa-e2e box carries the --sc-t-qa green accent on its border-left', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto(`/workstreams/${STORY_ID}?mode=impl&master=open`);

  const board = page.locator('.swarm-ctrl .phase-board');
  await expect(board).toBeVisible();

  const qaE2e = board.locator('.phase.phase--qae2e').first();
  await expect(qaE2e).toBeVisible();

  // The variant data-attribute is what the unit + visual contracts
  // both anchor on — gives the test a stable hook independent of
  // CSS class naming.
  await expect(qaE2e).toHaveAttribute('data-variant', 'qae2e');

  // The default (dark) theme's `--sc-t-qa` resolves to `--sc-green`
  // = `#79e08a` = `rgb(121, 224, 138)`. The `phase--qae2e` rule
  // sets a 3px green border-left on the card.
  await expect(qaE2e).toHaveCSS('border-left-color', 'rgb(121, 224, 138)');
  await expect(qaE2e).toHaveCSS('border-left-width', '3px');

  // Sanity: the impl sibling above does NOT carry the qa-e2e variant.
  const implSibling = board.locator('.phase').first();
  await expect(implSibling).not.toHaveClass(/phase--qae2e/);
  await expect(implSibling).toHaveAttribute('data-variant', 'impl');
});
