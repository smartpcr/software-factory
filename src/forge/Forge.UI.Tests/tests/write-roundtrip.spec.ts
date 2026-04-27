import { expect, test } from '@playwright/test';

// Phase 6.13 — write-path round trip. Exercises:
//   - POST /api/cards   (create local card)
//   - POST /api/prs/{id}/auto-merge  (set auto-merge)
//   - POST /api/prs/{id}/build/rerun (trigger build → fans out pr.changed + pr.log)
//   - GET  /api/audit                (audit middleware captured every write)
// Without a real ADO/GitHub configured the provider hop is a no-op; the test
// asserts the local cache + audit + idempotency surfaces all behave correctly.

test('writes round-trip with audit + idempotency', async ({ request }) => {
  const baseURL = test.info().project.use.baseURL;
  const apiBase = baseURL?.replace('5181', '5180') ?? 'http://localhost:5180';
  const user = `playwright-roundtrip-${Date.now()}`;
  const idemKey = `roundtrip-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

  // 1) Create a card (local-only — no PostTo so no provider call required).
  const cardId = `#rt-${Date.now()}`;
  const createResp = await request.post(`${apiBase}/api/cards`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user },
    data: {
      card: {
        id: cardId,
        epic: 'EPIC-024',
        col: 'backlog',
        type: 'task',
        title: 'roundtrip task',
        tags: ['phase6'],
        pts: 1,
        priority: 'low',
        agent: null,
        cost: 0,
      },
    },
  });
  expect(createResp.status(), await createResp.text()).toBeLessThan(300);

  // 2) Toggle auto-merge on a known seeded PR.
  const prId = encodeURIComponent('PR #234');
  const armResp = await request.post(`${apiBase}/api/prs/${prId}/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user },
    data: { autoMerge: true },
  });
  expect(armResp.status()).toBeLessThan(300);

  // 3) Trigger a build re-run — server returns 202 + flips cached state and
  //    SignalR fans out pr.log lines (observed end-to-end by /api/prs).
  const rerunResp = await request.post(`${apiBase}/api/prs/${prId}/build/rerun`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user },
  });
  expect(rerunResp.status()).toBeLessThan(300);
  const rerunPr = await rerunResp.json();
  expect(rerunPr.build?.status).toBe('running');

  // 4) Idempotency replay — repeating the auto-merge toggle with the same
  //    Idempotency-Key returns the cached body and an Idempotent-Replay header.
  const firstIdem = await request.post(`${apiBase}/api/prs/${prId}/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user, 'Idempotency-Key': idemKey },
    data: { autoMerge: false },
  });
  const replay = await request.post(`${apiBase}/api/prs/${prId}/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user, 'Idempotency-Key': idemKey },
    data: { autoMerge: true },
  });
  expect(firstIdem.status()).toBe(replay.status());
  expect(replay.headers()['idempotent-replay']).toBe('true');

  // 5) Audit log captured every write under this user.
  const auditResp = await request.get(`${apiBase}/api/audit?take=200`);
  const auditBody = await auditResp.text();
  expect(auditBody, 'audit must include the roundtrip user').toContain(user);

  // Cleanup so subsequent fidelity baselines stay stable.
  await request.post(`${apiBase}/api/prs/${prId}/auto-merge`, {
    headers: { 'Content-Type': 'application/json', 'X-Forge-User': user },
    data: { autoMerge: false },
  });
});
