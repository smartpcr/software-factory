#!/usr/bin/env python3
# -----------------------------------------------------------------------
# Forge active-workstream stall monitor.
# Snapshots active workstreams across all products, records transcript
# progress (totalCount + last-event time) to a state file, and flags any
# active/picked-up workstream that has not advanced since the last tick.
# -----------------------------------------------------------------------
import sys, json, os, time, urllib.request, datetime

BASE = "http://localhost:5180"
STATE = os.path.join(os.path.dirname(__file__), ".forge-monitor-state.json")
STALL_SECS = 600        # no new transcript event for 10 min => stalled
TERMINAL = {"complete", "historic", "cancelled", "done", "merged"}

def get(path, product=None):
    req = urllib.request.Request(BASE + path)
    if product:
        req.add_header("X-Forge-Product-Id", product)
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)

def now():
    return datetime.datetime.now(datetime.timezone.utc)

def parse_dt(s):
    if not s:
        return None
    try:
        return datetime.datetime.fromisoformat(s)
    except Exception:
        return None

def main():
    products = [p["id"] for p in get("/api/products")]
    prev = {}
    if os.path.exists(STATE):
        try:
            prev = json.load(open(STATE))
        except Exception:
            prev = {}

    snapshot = {}
    report = []
    nowt = now()
    for p in products:
        try:
            spawns = get("/api/stories/spawns", p)
        except Exception as e:
            report.append(f"[{p}] spawns query failed: {e}")
            continue
        active_spawns = [s for s in spawns if s.get("phase") in ("implement", "review", "decompose", "plan")]
        if not active_spawns:
            continue
        try:
            wss = get("/api/workstreams/", p)
        except Exception as e:
            report.append(f"[{p}] workstreams query failed: {e}")
            continue
        spawn_tags = {}
        for s in active_spawns:
            sid = s.get("storyId", "").split(":")[-1].lower().replace("_", "-")
            spawn_tags[sid] = s.get("phase")
        for w in wss:
            wid = w.get("id", "")
            status = w.get("status")
            if status in TERMINAL:
                continue
            # only workstreams under an active spawn
            if not any(tag in wid for tag in spawn_tags):
                continue
            # Terminal-failure statuses set by the orchestrator (stalled/blocked)
            # don't emit transcript events, so the age-based check below never
            # flags them — surface them explicitly with their stall reason.
            if status in ("stalled", "blocked"):
                reason = (w.get("stallReason") or "").strip()
                report.append(
                    f"[{p}] {status.upper():8} {wid.split('phase-')[-1]}"
                    f"  *** {status.upper()} ***  {reason[:200]}")
                snapshot[wid] = {"total": -1, "last_at": None, "status": status, "product": p}
                continue
            if status not in ("active", "review", "picked-up", "live"):
                continue
            try:
                tr = get(f"/api/work-items/{wid}/transcript?order=desc&limit=1", p)
                total = tr.get("totalCount", 0)
                ev = tr.get("events", [])
                last_at = ev[0].get("at") if ev else None
            except Exception as e:
                total, last_at = -1, None
            snapshot[wid] = {"total": total, "last_at": last_at, "status": status, "product": p}
            last_dt = parse_dt(last_at)
            age = (nowt - last_dt).total_seconds() if last_dt else None
            prev_total = prev.get(wid, {}).get("total")
            advanced = prev_total is not None and total != prev_total
            stalled = age is not None and age > STALL_SECS
            flag = ""
            if stalled:
                flag = f"  *** STALLED (no event for {int(age)}s) ***"
            elif age is not None and age > 180 and not advanced and prev_total is not None:
                flag = f"  ~ slow (last event {int(age)}s ago, no progress since last tick)"
            report.append(f"[{p}] {status:8} total={total:5} last={int(age) if age is not None else '?'}s ago  {wid.split('phase-')[-1]}{flag}")

    json.dump(snapshot, open(STATE, "w"), indent=2)
    print(f"=== monitor tick {nowt.isoformat(timespec='seconds')} | {len(snapshot)} active workstreams ===")
    for line in report:
        print(line)
    if not report:
        print("no active workstreams under active spawns")

if __name__ == "__main__":
    main()
