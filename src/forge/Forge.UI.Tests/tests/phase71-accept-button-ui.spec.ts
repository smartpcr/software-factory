import { expect, test } from '@playwright/test';

// Phase 71 — UI-level e2e: when a workstream has at least one
// deliverable, the WorkstreamConversationPane MUST render a
// ✓ ACCEPT button.
//
// This catches the exact regression the operator hit 2026-05-15: the
// Phase 70 backend ACCEPT endpoint was deployed but the button was
// invisible in the operator's browser (Vite HMR didn't propagate the
// file change AND no test ever asserted the button rendered against a
// real workstream). A bundle-string assertion doesn't work because
// Vite mangles identifiers in dev transform; the only reliable signal
// is the actual rendered DOM.
//
// The test discovers a real workstream-with-deliverable via the API,
// navigates to its conversation tab, and asserts the button exists.
// When no fixture is available (fresh CI DB), the test SKIPs with a
// clear message rather than false-failing.

const WRITER_HEADER = 'X-Forge-User';
const WRITER_USER = 'playwright@forge.local';

interface WorkstreamRow {
  id: string;
  status?: string;
  storySpawnId?: string;
}

interface SpawnSummary {
  storyId: string;
  phase?: string;
  id?: string;
}

async function findWorkstreamWithDeliverable(
  request: import('@playwright/test').APIRequestContext,
): Promise<{ workstreamId: string; storyId: string } | null> {
  // Step 1 — list story spawns. Each spawn is the anchor for a set of
  // workstreams; the /api/workstreams/ endpoint doesn't expose the
  // spawn linkage, so we go through the spawn detail endpoint.
  const spawnsResp = await request.get('/api/stories/spawns');
  if (!spawnsResp.ok()) return null;
  const spawns = await spawnsResp.json() as Array<{ id?: string; storyId: string; phase?: string }>;

  for (const spawn of spawns) {
    if (!spawn.storyId) continue;
    const detailResp = await request.get(`/api/stories/${encodeURIComponent(spawn.storyId)}/spawn`);
    if (!detailResp.ok()) continue;
    const detail = await detailResp.json() as { workstreams?: Array<{ id: string; status?: string }> };
    if (!detail.workstreams) continue;

    // Filter to active/review workstreams (not terminal).
    const candidates = detail.workstreams.filter(
      (w) => w.status !== 'cancelled' && w.status !== 'complete'
          && w.status !== 'historic' && w.status !== 'merging');

    for (const ws of candidates) {
      const execResp = await request.get(`/api/work-items/${encodeURIComponent(ws.id)}/execution`);
      if (!execResp.ok()) continue;
      const exec = await execResp.json() as { deliverables?: unknown[] };
      if (exec.deliverables && exec.deliverables.length > 0) {
        return { workstreamId: ws.id, storyId: spawn.storyId };
      }
    }
  }
  return null;
}

test.describe('Phase 71 — UI: ACCEPT button on workstream conversation tab', () => {
  test('renders the ✓ ACCEPT button when a deliverable exists', async ({ page, request }) => {
    const fixture = await findWorkstreamWithDeliverable(request);
    test.skip(
      fixture === null,
      'No workstream with deliverables found in the live DB — seed a spawn or '
      + 'rely on Phase70WorkstreamAcceptTests.cs for the equivalent in-process coverage.');
    if (fixture === null) return; // type narrow

    const storyId = encodeURIComponent(fixture.storyId);
    const wsId = encodeURIComponent(fixture.workstreamId);
    await page.goto(`/workstreams/${storyId}?mode=impl&sel=phase&selId=${wsId}&tab=conv`);

    // Set writer header AFTER navigation so localStorage is accessible.
    await page.evaluate((u) => window.localStorage.setItem('forge.user', u), WRITER_USER);

    // Wait for the conversation pane to mount. The toolbar is the
    // canonical anchor — it sits above the iter pager and always
    // renders when the pane is active.
    const acceptBtn = page.locator('button:has-text("ACCEPT")');

    await expect(
      acceptBtn,
      'WorkstreamConversationPane must render the ✓ ACCEPT button '
      + '(Phase 70). If this fails, the most likely cause is a stale '
      + 'Vite bundle in the test browser OR the button was removed '
      + 'from WorkstreamConversationPane.tsx during a refactor.',
    ).toBeVisible({ timeout: 30_000 });

    // And it should be ENABLED — a deliverable exists, the workstream
    // is non-terminal, and no other operation is in flight.
    await expect(acceptBtn).toBeEnabled();
  });
});
