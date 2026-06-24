import sqlite3, json

db = r"E:\forge\code-intelligence.db"
con = sqlite3.connect(db)
con.row_factory = sqlite3.Row
cur = con.cursor()

# Find the dynamic-dispatch-synthesizer workstream
cur.execute("""
SELECT Id, Name, Status, StallReason, PausedFromStatus, Summary, PrsJson
FROM workstreams
WHERE Id LIKE '%dynamic-dispatch-synthesizer%' OR Name LIKE '%Dynamic-dispatch sy%'
""")
rows = cur.fetchall()
for r in rows:
    print("=" * 100)
    print("WS Id     :", r["Id"])
    print("Name      :", r["Name"])
    print("Status    :", r["Status"])
    print("PausedFrom:", r["PausedFromStatus"])
    print("StallReason:", r["StallReason"])
    print("PrsJson   :", r["PrsJson"])
    print("Summary   :", (r["Summary"] or "")[:300])
    wsid = r["Id"]

    # latest deliverables for this ws
    cur.execute("PRAGMA table_info(deliverables)")
    dcols = [c[1] for c in cur.fetchall()]
    print("\n  deliverables cols:", ", ".join(dcols))
    # guess workstream key column
    keycol = "WorkstreamId" if "WorkstreamId" in dcols else ("WorkItemId" if "WorkItemId" in dcols else None)
    if keycol:
        cur.execute(f"""SELECT * FROM deliverables WHERE {keycol}=? ORDER BY rowid DESC LIMIT 5""", (wsid,))
        for d in cur.fetchall():
            dd = dict(d)
            slim = {k: dd[k] for k in dd if k.lower() in ("iter","iteration","verdict","score","failurekind","createdat","notes")}
            if "Notes" in slim and slim["Notes"]:
                slim["Notes"] = slim["Notes"][:200]
            print("   ", slim)
con.close()
