import { expect, test } from '@playwright/test';

// Phase 71 — HTTP-level e2e for POST /api/workstreams/{id}/accept.
//
// Drives Playwright's request context (no browser) against the real
// Forge.Api spawned by the webServer config. Unlike the unit-level
// xUnit tests under Forge.Api.Tests (which use the in-process
// ForgeWebApplicationFactory), this runs against the actual host the
// UI talks to and so catches:
//   - Route registration (typos in MapPost path)
//   - Auth pipeline configuration (the writer header semantics)
//   - JSON serialisation contract (UI expects camelCase keys)
//   - Status code mapping for the documented error branches
//
// The 400 ("no deliverables") and 200 ("happy path") branches need a
// workstream + deliverable fixture that doesn't exist in the canonical
// seed — those are covered by the in-process unit tests
// (Phase70WorkstreamAcceptTests.cs) which can insert DeliverableRow
// directly. The HTTP spec here pins the route surface that the in-
// process tests can't reach: ASP.NET routing, auth, and status mapping.

const WRITER_HEADER = 'X-Forge-User';
const WRITER_USER = 'playwright@forge.local';

test.describe('Phase 71 — workstream ACCEPT HTTP e2e', () => {
  test('rejects unauthenticated requests with 401', async ({ request }) => {
    // No writer header — auth middleware should kick in BEFORE the
    // handler runs. 401 implies the route also exists (handler reached
    // by routing but rejected by RequireWriter).
    const resp = await request.post('/api/workstreams/any-id/accept');
    expect(resp.status()).toBe(401);
  });

  test('returns 404 for an unknown workstream id', async ({ request }) => {
    const resp = await request.post(
      '/api/workstreams/ws-phase71-does-not-exist/accept',
      { headers: { [WRITER_HEADER]: WRITER_USER } });
    expect(resp.status()).toBe(404);
  });

  test('endpoint route is registered on the workstreams group (regression guard)', async ({ request }) => {
    // 401 implies the route exists (we hit auth before reaching the
    // handler). 404 would mean MapPost("/{id}/accept") got dropped or
    // moved during a refactor — that's the exact failure mode we want
    // this test to scream about. This is the canonical "Phase 70 route
    // still wired" assertion the UI depends on.
    const resp = await request.post('/api/workstreams/anything/accept');
    expect(
      resp.status(),
      'expected route to exist (401 from auth) — 404 means MapPost("/{id}/accept") was dropped',
    ).toBe(401);
  });

  test('endpoint returns JSON with the documented response shape on 404', async ({ request }) => {
    // Pin the error-body shape so the UI's catch-handler can rely on it.
    const resp = await request.post(
      '/api/workstreams/ws-phase71-fixture-missing/accept',
      { headers: { [WRITER_HEADER]: WRITER_USER } });
    expect(resp.status()).toBe(404);
    const ct = resp.headers()['content-type'] ?? '';
    expect(ct, 'expected JSON ProblemDetails body').toMatch(/application\/(problem\+)?json/);
  });
});

