import sqlite3, json, sys

db = sqlite3.connect(r'E:\forge\forge.db')
db.row_factory = sqlite3.Row
cur = db.cursor()

print('=== TABLES ===')
for t in cur.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").fetchall():
    print('  ', t[0])

wsid = 'ws-qq-microsoft-teams-mess-phase-teams-bot-framework-core-stage-asp-net-core-bot-host'

print('\n=== Workstream row ===')
r = cur.execute('SELECT * FROM Workstreams WHERE Id = ?', (wsid,)).fetchone()
if r:
    for k in r.keys():
        v = r[k]
        if isinstance(v, str) and len(v) > 400:
            v = v[:400] + ' …(' + str(len(r[k])) + ' chars)'
        print(f'  {k}: {v}')

print('\n=== work_items row (Id matches WorkstreamId) ===')
r = cur.execute('SELECT * FROM work_items WHERE Id = ?', (wsid,)).fetchone()
if r:
    for k in r.keys():
        v = r[k]
        if isinstance(v,str) and len(v)>400: v = v[:400]+' …'
        print(f'  {k}: {v}')

def schema(table):
    try:
        cols = [c[1] for c in cur.execute(f'PRAGMA table_info({table})').fetchall()]
        print(f'\n--- schema({table}) = {cols}')
        return cols
    except Exception as e:
        print(f'  schema({table}) failed: {e}')
        return []

for t in ('deliverables','task_queue','prs','ws_events','build_validation_runs','build_stages','agent_pair_assignment','agent_usage'):
    schema(t)

print('\n=== task_queue rows mentioning this WS ===')
try:
    cols = [c[1] for c in cur.execute('PRAGMA table_info(task_queue)').fetchall()]
    sel = ','.join(cols)
    rows = cur.execute(f'SELECT {sel} FROM task_queue WHERE Id LIKE ? OR ' + ' OR '.join([f'{c} LIKE ?' for c in cols if c.lower() in ('parentid','workitemid','taskid','target','payload','metadatajson')]),
                       tuple([f'%{wsid}%'] * (1 + sum(1 for c in cols if c.lower() in ('parentid','workitemid','taskid','target','payload','metadatajson'))))).fetchall()
    print(f'  {len(rows)} rows')
    for r in rows[:10]:
        d = dict(zip(cols,r))
        for k,v in list(d.items()):
            if isinstance(v,str) and len(v)>200: d[k]=v[:200]+' …'
        print(' ', d)
except Exception as e:
    print('  failed:', e)

print('\n=== prs for the branch ===')
try:
    cols = [c[1] for c in cur.execute('PRAGMA table_info(prs)').fetchall()]
    rows = cur.execute('SELECT * FROM prs WHERE ' + ' OR '.join([f'{c} LIKE ?' for c in cols if c.lower() in ('branch','headref','head','workitemid','workstreamid','id')]),
                       tuple([f'%{wsid[-50:]}%']*sum(1 for c in cols if c.lower() in ('branch','headref','head','workitemid','workstreamid','id')))).fetchall()
    for r in rows:
        d = dict(zip(cols,r))
        for k,v in list(d.items()):
            if isinstance(v,str) and len(v)>200: d[k]=v[:200]+' …'
        print(' ', d)
except Exception as e:
    print('  failed:', e)

print('\n=== build_validation_runs for this WS ===')
try:
    cols = [c[1] for c in cur.execute('PRAGMA table_info(build_validation_runs)').fetchall()]
    rows = cur.execute('SELECT * FROM build_validation_runs WHERE ' + ' OR '.join([f'{c} = ?' for c in cols if c.lower() in ('workitemid','workstreamid','taskid')]) + ' ORDER BY rowid DESC LIMIT 10',
                       tuple([wsid]*sum(1 for c in cols if c.lower() in ('workitemid','workstreamid','taskid')))).fetchall()
    print(f'  {len(rows)} rows')
    for r in rows:
        d = dict(zip(cols,r))
        for k,v in list(d.items()):
            if isinstance(v,str) and len(v)>200: d[k]=v[:200]+' …'
        print(' ', d)
except Exception as e:
    print('  failed:', e)

print('\n=== Most-recent task_transcript_event for this WS ===')
try:
    cols = [c[1] for c in cur.execute('PRAGMA table_info(task_transcript_event)').fetchall()]
    print('  cols:', cols)
    # try taskid
    rows = cur.execute('SELECT * FROM task_transcript_event WHERE TaskId LIKE ? ORDER BY rowid DESC LIMIT 15', (f'%{wsid}%',)).fetchall()
    for r in rows:
        d = dict(zip(cols,r))
        for k,v in list(d.items()):
            if isinstance(v,str) and len(v)>250: d[k]=v[:250]+' …'
        print(' ', d)
except Exception as e:
    print('  failed:', e)
