// -----------------------------------------------------------------------
// Phase 18.5 / 18.6 — Playwright global setup. The React app reads its
// identity from `/api/me`, which resolves via the connected auth
// provider (ADO / GitHub), NOT the `X-Forge-User` header. So overriding
// the header in the browser context can't make the UI think we're a PO.
//
// To exercise the role-gated paths end-to-end we mutate the dev DB
// directly: promote the currently-signed-in user to product-owner before
// the suite, save the original role, and restore it in the teardown.
// The DB is in WAL mode so concurrent writes from this script and the
// running Forge.Api are safe.
// -----------------------------------------------------------------------

import Database from 'better-sqlite3';
import { existsSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const DB_CANDIDATES = [
  resolve(process.cwd(), '../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../../forge/Forge.Api/forge.db'),
  resolve(process.cwd(), 'src/forge/Forge.Api/forge.db'),
];

// Snapshot file lives next to the DB so a Ctrl-C between runs doesn't
// leave the role permanently flipped — the teardown reads this file and
// restores. Keeping it under the test dir keeps it git-ignored alongside
// other transient artefacts.
const SNAPSHOT_PATH = resolve(process.cwd(), 'e2e/.role-snapshot.json');

export default async function globalSetup(): Promise<void> {
  const dbPath = DB_CANDIDATES.find((p) => existsSync(p));
  if (!dbPath) {
    throw new Error(
      `e2e globalSetup: could not find forge.db. Tried:\n  ${DB_CANDIDATES.join('\n  ')}\n` +
      'Start Forge.Api at least once so the DB exists, then rerun.',
    );
  }

  const db = new Database(dbPath);
  db.pragma('journal_mode = WAL');

  // Collect every user row so we can restore exact role state in teardown.
  const rows = db.prepare('SELECT Id, Email, Role FROM Users').all() as Array<{
    Id: string;
    Email: string;
    Role: string;
  }>;

  if (rows.length === 0) {
    db.close();
    throw new Error(
      'e2e globalSetup: Users table is empty. Sign in to Forge.Api at least once ' +
      'so /api/me lazy-creates the bootstrap user, then rerun.',
    );
  }

  writeFileSync(SNAPSHOT_PATH, JSON.stringify(rows, null, 2));

  // Promote every existing user to product-owner so the default browser
  // context (driven by ADO/GitHub auth) renders the PO-editable surface.
  // The non-PO test handles its own demote/restore around the test body.
  const update = db.prepare("UPDATE Users SET Role = 'product-owner' WHERE Id = ?");
  for (const r of rows) update.run(r.Id);

  db.close();
  console.log(`[e2e setup] promoted ${rows.length} user(s) to product-owner; snapshot at ${SNAPSHOT_PATH}`);
}
