// -----------------------------------------------------------------------
// docs/forge/e2e.md Stage 8.2 — Playwright smoke for the workstream DAG
// edge router updates:
//
//   1. The previous "rightmost-column QA cluster" rendering is gone —
//      qa-e2e cards now stack DIRECTLY BENEATH their impl sibling
//      (same `left`, larger `top`) rather than landing in a separate
//      column far to the right of the impl rows.
//
//   2. Clicking a qa-e2e box opens the workstream detail panel scoped
//      to that row (same drill-down as impl boxes) — verified by the
//      URL transition to the qa-e2e workstream's `selId`.
//
// Strategy mirrors the Stage 8.1 spec: mock `/api/stories/{id}/spawn`
// + `/spawn/artifacts` via `page.route` so the test is self-contained
// and doesn't require seeding a real spawn through the orchestrator.
// Forge.Api must still be reachable for the `/api/me` auth check.
// -----------------------------------------------------------------------

import { expect, test, type Page } from '@playwright/test';

const API = 'http://localhost:5180';
const STORY_ID = 'stage82-dag-fixture';

// 3 impl stages, 2 with qa-e2e siblings (stages A + C), one without (B).
// Identical shape to the Stage 8.1 fixture so the assertions read
// against the same mental model the operator built last stage.
const SPAWN_DETAIL_FIXTURE = {
  spawn: {
    id: 'spawn-' + STORY_ID,
    storyId: STORY_ID,
    storyTitle: 'Stage 8.2 DAG fixture',
    repoId: 'repo-stage82',
    // 'decompose' phase puts the SwarmStoryView in `design` mode by
    // default — the DAG view opens after clicking the decomposition
    // doc card. The Playwright spec explicitly navigates to the
    // decomposition sel so we don't depend on the operator click.
    phase: 'decompose',
    planBranch: null,
    planPrId: null,
    decompositionJson: JSON.stringify([
      { title: 'A — alpha', deps: [] },
      { title: 'B — beta', deps: ['A — alpha'] },
      { title: 'C — gamma', deps: ['B — beta'] },
    ]),
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
    implRow('ws-stage82-a', 'A — alpha', 'phase-foundation/stage-a', 0, []),
    qaE2eRow('ws-stage82-a-e2e', 'A — E2E', 'phase-foundation/stage-a/e2e', 1, ['ws-stage82-a']),
    implRow('ws-stage82-b', 'B — beta', 'phase-foundation/stage-b', 2, ['ws-stage82-a']),
    implRow('ws-stage82-c', 'C — gamma', 'phase-foundation/stage-c', 3, ['ws-stage82-b']),
    qaE2eRow('ws-stage82-c-e2e', 'C — E2E', 'phase-foundation/stage-c/e2e', 4, ['ws-stage82-c']),
  ],
};

function implRow(id: string, name: string, planAnchor: string, sort: number, deps: string[]) {
  return {
    id,
    name,
    branch: `ws/${id}`,
    status: 'active',
    cards: [],
    prs: [],
    deps,
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

function qaE2eRow(id: string, name: string, planAnchor: string, sort: number, deps: string[]) {
  return { ...implRow(id, name, planAnchor, sort, deps), category: 'qa-e2e' };
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

const DAG_URL = `/workstreams/${STORY_ID}?mode=design&master=open&sel=doc&selId=doc:decomposition`;

// ── 1. [ui-e2e] Rightmost QA column is gone — qa-e2e stacks beneath impl ──

test('qa-e2e cards stack beneath their impl sibling (no rightmost-column QA cluster)', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto(DAG_URL);

  // The DAG view's phase container — `.ws-phase__body` is the
  // absolute-positioned canvas for stage cards.
  const dag = page.locator('.ws-dag .ws-phase__body').first();
  await expect(dag).toBeVisible();

  // 5 stage cards total: 3 impl + 2 qa-e2e. Pre-Stage-8.2 the
  // qa-e2e cards landed as standalone column nodes (depth=1, all
  // bunched in a "rightmost column" since they all depended on their
  // impl sibling). After Stage 8.2 they stack vertically beneath
  // their impl.
  const allStages = dag.locator('.ws-stage');
  await expect(allStages).toHaveCount(5);

  // The qa-e2e cards carry `data-ws-category="qa-e2e"`.
  const qaCards = dag.locator('.ws-stage[data-ws-category="qa-e2e"]');
  await expect(qaCards).toHaveCount(2);

  // Each qa-e2e card shares its impl sibling's `left` (same column)
  // and sits BELOW it (larger `top`). Measure via getBoundingClientRect
  // and compare.
  const pairs: Array<{ qaId: string; implId: string }> = [
    { qaId: 'ws-stage82-a-e2e', implId: 'ws-stage82-a' },
    { qaId: 'ws-stage82-c-e2e', implId: 'ws-stage82-c' },
  ];
  for (const { qaId, implId } of pairs) {
    const qaBox = await dag.locator(`.ws-stage[data-ws-id="${qaId}"]`).boundingBox();
    const implBox = await dag.locator(`.ws-stage[data-ws-id="${implId}"]`).boundingBox();
    expect(qaBox).not.toBeNull();
    expect(implBox).not.toBeNull();
    // Same x (allow 1px FP tolerance) — stacked in the same column.
    expect(Math.abs(qaBox!.x - implBox!.x)).toBeLessThan(1.5);
    // qa-e2e sits directly below — top is greater than impl's top
    // but within a stage height + small gap.
    expect(qaBox!.y).toBeGreaterThan(implBox!.y);
    const gap = qaBox!.y - (implBox!.y + implBox!.height);
    expect(gap).toBeGreaterThanOrEqual(0);
    expect(gap).toBeLessThan(40); // WS_QAE2E_STACK_GAP=8 + scroll/borders tolerance
  }

  // Pre-Stage-8.2 anti-regression: no qa-e2e card is positioned to
  // the RIGHT of all impl cards (which is what the old "rightmost
  // column" layout produced). Compute max impl-card left, assert no
  // qa-e2e card has a left > that.
  const implCards = dag.locator('.ws-stage[data-ws-category="impl"]');
  const implCount = await implCards.count();
  let maxImplLeft = 0;
  for (let i = 0; i < implCount; i++) {
    const box = await implCards.nth(i).boundingBox();
    if (box && box.x > maxImplLeft) maxImplLeft = box.x;
  }
  const qaCount = await qaCards.count();
  for (let i = 0; i < qaCount; i++) {
    const box = await qaCards.nth(i).boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeLessThanOrEqual(maxImplLeft + 1.5);
  }
});

// ── 2. [ui-e2e] Clicking a qa-e2e box drills down into its workstream ────

test('clicking a qa-e2e box opens the workstream detail panel scoped to that row', async ({ page }) => {
  await installFixtureRoutes(page);
  await page.goto(DAG_URL);

  const dag = page.locator('.ws-dag .ws-phase__body').first();
  await expect(dag).toBeVisible();

  const qaCard = dag.locator('.ws-stage[data-ws-id="ws-stage82-a-e2e"]');
  await expect(qaCard).toBeVisible();
  await expect(qaCard).toHaveAttribute('data-ws-category', 'qa-e2e');

  // The card is keyboard-focusable + click-handled. After click,
  // `openWorkstream` navigates to /workstreams/{storyId}?mode=impl&
  // master=open&sel=phase&selId={qaId}. URL transitions confirm the
  // drill-down works for qa-e2e cards identically to impl cards.
  await qaCard.click();

  await expect(page).toHaveURL(/sel=phase/);
  await expect(page).toHaveURL(/selId=ws-stage82-a-e2e/);
  await expect(page).toHaveURL(/mode=impl/);
});
