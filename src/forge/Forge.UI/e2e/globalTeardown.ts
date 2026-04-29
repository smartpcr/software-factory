// -----------------------------------------------------------------------
// Phase 18.5 / 18.6 — Playwright teardown. Restores every user's
// original role from the snapshot written by globalSetup.ts. Idempotent:
// if the snapshot is missing (e.g. setup failed) we no-op rather than
// rewrite the DB with stale data.
// -----------------------------------------------------------------------

import Database from 'better-sqlite3';
import { existsSync, readFileSync, unlinkSync } from 'node:fs';
import { resolve } from 'node:path';

const DB_CANDIDATES = [
  resolve(process.cwd(), '../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../Forge.Api/forge.db'),
  resolve(process.cwd(), '../../../forge/Forge.Api/forge.db'),
  resolve(process.cwd(), 'src/forge/Forge.Api/forge.db'),
];

const SNAPSHOT_PATH = resolve(process.cwd(), 'e2e/.role-snapshot.json');

export default async function globalTeardown(): Promise<void> {
  if (!existsSync(SNAPSHOT_PATH)) {
    console.log('[e2e teardown] no snapshot found — nothing to restore');
    return;
  }

  const dbPath = DB_CANDIDATES.find((p) => existsSync(p));
  if (!dbPath) return;

  const rows = JSON.parse(readFileSync(SNAPSHOT_PATH, 'utf-8')) as Array<{
    Id: string;
    Email: string;
    Role: string;
  }>;

  const db = new Database(dbPath);
  const update = db.prepare('UPDATE Users SET Role = ? WHERE Id = ?');
  for (const r of rows) update.run(r.Role, r.Id);
  db.close();

  unlinkSync(SNAPSHOT_PATH);
  console.log(`[e2e teardown] restored ${rows.length} user role(s)`);
}
