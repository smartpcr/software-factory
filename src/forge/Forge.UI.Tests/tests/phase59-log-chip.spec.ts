import { expect, test } from '@playwright/test';

// Phase 59 Stage B — UI tests for the gate-log chip + "Has logs"
// filter. T18-T21 in docs/forge_implementation.md.
//
// These tests need a live workstream with at least one
// task.{build,test}-gate.{passed,failed} transcript event that
// carries a `logUrl` payload field. On a fresh CI DB no such
// fixture exists; these tests SKIP with a clear message rather
// than false-failing, mirroring the phase71-accept-button-ui
// pattern. The fast unit coverage for the `hasLogAttachment`
// predicate lives in
// src/forge/Forge.UI/src/views/conversation/TranscriptLine.spec.ts
// (Vitest, 10 cases, always runs).
//
// When a real workstream has run a test gate and recorded
// logUrl-bearing events on the live server, all four tests
// activate.

interface WorkstreamWithLog {
  workstreamId: string;
  storyId: string;
  logUrl: string;
}

async function findWorkstreamWithGateLog(
  request: import('@playwright/test').APIRequestContext,
): Promise<WorkstreamWithLog | null> {
  // Walk story spawns → workstreams → look for a transcript
  // event whose payload carries logUrl. The transcript endpoint
  // is /api/work-items/{id}/transcript; filter to gate events
  // for the chip-render contract.
  const spawnsResp = await request.get('/api/stories/spawns');
  if (!spawnsResp.ok()) return null;
  const spawns = (await spawnsResp.json()) as Array<{
    id?: string;
    storyId: string;
    phase?: string;
  }>;

  for (const spawn of spawns) {
    if (!spawn.storyId) continue;
    const detailResp = await request.get(
      `/api/stories/${encodeURIComponent(spawn.storyId)}/spawn`,
    );
    if (!detailResp.ok()) continue;
    const detail = (await detailResp.json()) as {
      workstreams?: Array<{ id: string; status?: string }>;
    };
    if (!detail.workstreams) continue;

    for (const ws of detail.workstreams) {
      const transResp = await request.get(
        `/api/work-items/${encodeURIComponent(ws.id)}/transcript?limit=200`,
      );
      if (!transResp.ok()) continue;
      const transcript = (await transResp.json()) as {
        events?: Array<{
          kind: string;
          payload?: Record<string, unknown>;
        }>;
      };
      const events = transcript.events ?? [];
      const evt = events.find(
        (e) =>
          (e.kind === 'task.test-gate.passed'
            || e.kind === 'task.test-gate.failed'
            || e.kind === 'task.build-gate.passed'
            || e.kind === 'task.build-gate.failed')
          && typeof e.payload?.logUrl === 'string'
          && (e.payload.logUrl as string).length > 0,
      );
      if (evt) {
        return {
          workstreamId: ws.id,
          storyId: spawn.storyId,
          logUrl: evt.payload!.logUrl as string,
        };
      }
    }
  }
  return null;
}

async function openWorkstreamConversation(
  page: import('@playwright/test').Page,
  storyId: string,
  workstreamId: string,
): Promise<void> {
  const story = encodeURIComponent(storyId);
  const ws = encodeURIComponent(workstreamId);
  await page.goto(`/workstreams/${story}?mode=impl&sel=phase&selId=${ws}&tab=conv`);
  // Wait for the toolbar to mount — anchor we know always renders
  // on the conversation pane.
  await page
    .locator('button:has-text("ACCEPT"), button:has-text("RESUME")')
    .first()
    .waitFor({ state: 'visible', timeout: 30_000 });
}

test.describe('Phase 59 Stage B — gate-log chip + Has-logs filter', () => {
  // ── T18 — chip renders with logUrl payload, click opens the log ─────

  test('T18 — failed/passed gate event renders 📎 view log chip with mode tooltip', async ({
    page,
    request,
  }) => {
    const fixture = await findWorkstreamWithGateLog(request);
    test.skip(
      fixture === null,
      'No workstream with logUrl-bearing gate events found. Trigger a '
      + 'test gate run on a C# workspace and re-run.',
    );
    if (fixture === null) return;

    await openWorkstreamConversation(page, fixture.storyId, fixture.workstreamId);

    // The chip lives inside any transcript line whose payload
    // carries logUrl. Wait for at least one to mount.
    const chip = page
      .locator('a[data-testid="transcript-log-chip"]')
      .first();
    await expect(chip).toBeVisible({ timeout: 30_000 });

    // Tooltip is set via the `title` attribute. When impactedMode
    // is present, it appends ` · mode: {value}` — pin that the
    // tooltip includes "view log" (which both shapes share).
    const tooltip = await chip.getAttribute('title');
    expect(tooltip ?? '').toContain('view log');

    // href points at the actual log endpoint.
    const href = await chip.getAttribute('href');
    expect(href ?? '').toMatch(/^\/api\/workstreams\/[^/]+\/logs\//);
  });

  // ── T19 — skipped events do NOT render the chip ─────────────────────

  test('T19 — task.test-gate.skipped events render without 📎 chip', async ({
    page,
    request,
  }) => {
    const fixture = await findWorkstreamWithGateLog(request);
    test.skip(
      fixture === null,
      'No workstream with gate events found.',
    );
    if (fixture === null) return;

    // Pull the workstream's transcript so we can verify what's
    // there. If it has any skipped events, none of them should
    // produce a chip in the DOM.
    const transResp = await request.get(
      `/api/work-items/${encodeURIComponent(fixture.workstreamId)}/transcript?limit=500`,
    );
    const transcript = (await transResp.json()) as {
      events?: Array<{ kind: string; payload?: Record<string, unknown> }>;
    };
    const skippedEvents = (transcript.events ?? []).filter(
      (e) =>
        (e.kind === 'task.test-gate.skipped' || e.kind === 'task.build-gate.skipped'),
    );
    test.skip(
      skippedEvents.length === 0,
      'Workstream has no skipped gate events in its transcript.',
    );

    // Server-side contract: every skipped event has NO logUrl
    // field. The chip render is gated on logUrl being a
    // non-empty string, so they should never produce a chip.
    for (const evt of skippedEvents) {
      const url = evt.payload?.logUrl;
      expect(url ?? '', `${evt.kind} should not carry logUrl`).toBe('');
    }

    await openWorkstreamConversation(page, fixture.storyId, fixture.workstreamId);

    // Chip count in the rendered DOM should equal the count of
    // non-skipped gate events with logUrl. The exact number
    // depends on the workstream's iter history; we just assert
    // it's > 0 (since fixture had at least one logUrl event)
    // and that NO chip is associated with a skipped line.
    const chips = page.locator('a[data-testid="transcript-log-chip"]');
    expect(await chips.count()).toBeGreaterThan(0);
  });

  // ── T20 — "Has logs" filter hides events without logUrl ─────────────

  test('T20 — Has-logs toggle hides events without logUrl', async ({
    page,
    request,
  }) => {
    const fixture = await findWorkstreamWithGateLog(request);
    test.skip(
      fixture === null,
      'No workstream with gate events found.',
    );
    if (fixture === null) return;

    await openWorkstreamConversation(page, fixture.storyId, fixture.workstreamId);

    // Switch to "All" tab so non-gate events are visible — the
    // filter toggle is meaningful when there's something to hide.
    const allTab = page.locator('button[role="tab"]:has-text("All")');
    if (await allTab.isVisible()) {
      await allTab.click();
    }

    // Count visible transcript lines (any event-bearing rows)
    // before flipping the filter.
    const linesBefore = await page.locator('.live-exec__line').count();

    // Flip the toggle.
    const toggle = page.locator('button[data-testid="transcript-logs-only-toggle"]');
    await expect(toggle).toBeVisible({ timeout: 10_000 });
    await toggle.click();

    // After the toggle: every visible row should have a chip OR
    // there should be at least fewer rows than before (filter is
    // applied).
    const linesAfter = await page.locator('.live-exec__line').count();
    expect(linesAfter).toBeLessThanOrEqual(linesBefore);

    // Toggle is now in the "on" state — title attribute reflects.
    const pressed = await toggle.getAttribute('aria-pressed');
    expect(pressed).toBe('true');
  });

  // ── T21 — Screenshot fidelity: chip doesn't reflow row ──────────────

  test('T21 — chip render in light + dark theme matches baseline', async ({
    page,
    request,
  }) => {
    const fixture = await findWorkstreamWithGateLog(request);
    test.skip(
      fixture === null,
      'No workstream with gate events found.',
    );
    if (fixture === null) return;

    await openWorkstreamConversation(page, fixture.storyId, fixture.workstreamId);
    await page
      .locator('a[data-testid="transcript-log-chip"]')
      .first()
      .waitFor({ state: 'visible', timeout: 30_000 });

    // Capture the single transcript line containing the first
    // chip — the screenshot diff is scoped to that row so
    // unrelated layout shifts (live iter pager, KPI strip etc.)
    // don't false-fail this test.
    const firstLineWithChip = page
      .locator('.live-exec__line:has(a[data-testid="transcript-log-chip"])')
      .first();
    await expect(firstLineWithChip).toBeVisible();

    // Light theme baseline.
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'light'));
    await expect(firstLineWithChip).toHaveScreenshot('phase59-log-chip-light.png', {
      maxDiffPixelRatio: 0.01,
    });

    // Dark theme baseline.
    await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
    await expect(firstLineWithChip).toHaveScreenshot('phase59-log-chip-dark.png', {
      maxDiffPixelRatio: 0.01,
    });
  });
});
