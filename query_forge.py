"""Verify Phase 64 migration applied: deliverables.FailureKind + FailureReason columns."""
import sqlite3

DB = r'E:\forge\forge.db'
con = sqlite3.connect(DB)
con.row_factory = sqlite3.Row
cur = con.cursor()
cur.execute("PRAGMA table_info(deliverables)")
cols = [r['name'] for r in cur.fetchall()]
print("deliverables columns:", cols)
print("FailureKind present:", 'FailureKind' in cols)
print("FailureReason present:", 'FailureReason' in cols)
