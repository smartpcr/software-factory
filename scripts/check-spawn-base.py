import sqlite3
db = sqlite3.connect(r'E:\forge\forge.db')
cur = db.cursor()
cur.execute('PRAGMA table_info(story_spawns)')
spawn_cols = [r[1] for r in cur.fetchall()]
print('story_spawns cols:', spawn_cols)
print()
sel = [c for c in spawn_cols if c.lower() in ('id','basebranch','base','repoid','status','state','storyid')]
row = cur.execute(f"SELECT {','.join(sel)} FROM story_spawns WHERE Id = ?", ('spawn-qq-MICROSOFT-TEAMS-MESS',)).fetchone()
print('--- spawn row ---')
if row:
    for c,v in zip(sel,row): print(f'  {c}: {v}')
else:
    print('  NOT FOUND')
    rows = cur.execute("SELECT Id FROM story_spawns LIMIT 20").fetchall()
    print('  existing spawn ids:', [r[0] for r in rows])
print()
row = cur.execute('SELECT Id, DefaultBranch, CloneRoot FROM repos WHERE Id = ?', ('qq',)).fetchone()
print('--- repos[qq] ---')
for c,v in zip(('Id','DefaultBranch','CloneRoot'),row): print(f'  {c}: {v}')
