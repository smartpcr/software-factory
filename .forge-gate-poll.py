import sqlite3, json, time, sys
WS='ws-local-refactor-integration-phase-test-migration-simple-classes-stage-migrate-cachecontrollerintegrationtests'
CUT='2026-05-21 03:19:43'   # restart marker (UTC, second precision)
def pl(p):
    try: return json.loads(p)
    except: return {}
deadline=time.time()+560   # ~9.3 min, under the 10-min bg cap
seen_progress=set()
while time.time()<deadline:
    try:
        c=sqlite3.connect('file:E:/forge/local.db?mode=ro',uri=True).cursor()
        rows=list(c.execute("select Id,At,Kind,PayloadJson from task_transcript_event where WorkItemId=? and Kind in ('task.iteration.started','task.build-gate.passed','task.build-gate.failed','task.test-gate.passed','task.test-gate.failed','task.test-gate.skipped','task.test-gate.relaxed') order by Id",(WS,)))
    except Exception as e:
        time.sleep(20); continue
    for Id,At,k,p in rows:
        if (At or '')[:19] <= CUT: continue   # before restart
        if k.startswith('task.test-gate'):
            d=pl(p)
            keep={x:d.get(x) for x in ('iter','impactedMode','effectiveTestCommand','testCommand','reason','allTestProjectCount','wholeSuiteFallback') if x in d}
            print(f"FOUND test-gate @ {At}: {k}")
            print(json.dumps(keep, indent=2))
            sys.exit(0)
        else:
            key=(Id,k)
            if key not in seen_progress:
                seen_progress.add(key)
                print(f"progress @ {At}: {k} {pl(p).get('iter','')}")
    time.sleep(25)
print("TIMEOUT: no post-restart test-gate event yet (iteration still in build/generate). Re-run poller to keep watching.")
