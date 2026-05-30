#!/usr/bin/env python3
# -----------------------------------------------------------------------
# Focused stall monitor for the REFACTOR-GUIDE spawn (product
# code-intelligence). Tracks active workstream transcript progress
# (totalCount + last-event age) between ticks and flags any active/
# picked-up/review workstream that has not advanced or has gone quiet,
# plus any orchestrator terminal-failure status (stalled/blocked).
# -----------------------------------------------------------------------
import sys, json, os, time, datetime, urllib.request

BASE = "http://localhost:5180"
PRODUCT = "code-intelligence"
SLUG = "refactor-guide"
STATE = os.path.join(os.path.dirname(__file__), ".refactor-guide-monitor-state.json")
STALL_SECS = 600          # no new transcript event for 10 min => stalled
SLOW_SECS = 180           # quiet + no progress since last tick => slow
TERMINAL = {"complete", "historic", "cancelled", "done", "merged"}
LIVE = {"active", "review", "picked-up", "live"}


def get(path):
    req = urllib.request.Request(BASE + path)
    req.add_header("X-Forge-Product-Id", PRODUCT)
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


def load_prev():
    if os.path.exists(STATE):
        try:
            return json.load(open(STATE))
        except Exception:
            return {}
    return {}


def tick(prev):
    """Returns (counts, report_lines, snapshot, alerts). alerts = list of
    actionable strings (STALLED / slow / BLOCKED)."""
    nowt = now()
    snapshot, report, alerts = {}, [], []
    counts = {}
    wss = get("/api/workstreams/")
    for w in wss:
        wid = w.get("id", "")
        if SLUG not in wid:
            continue
        status = w.get("status")
        counts[status] = counts.get(status, 0) + 1
        if status in TERMINAL:
            continue
        short = wid.split("phase-")[-1]
        if status in ("stalled", "blocked"):
            reason = (w.get("stallReason") or "").strip()
            line = f"  {status.upper():8} {short}  *** {status.upper()} *** {reason[:200]}"
            report.append(line)
            alerts.append(line.strip())
            snapshot[wid] = {"total": -1, "last_at": None, "status": status}
            continue
        if status not in LIVE:
            continue
        try:
            tr = get(f"/api/work-items/{wid}/transcript?order=desc&limit=1")
            total = tr.get("totalCount", 0)
            ev = tr.get("events", [])
            last_at = ev[0].get("at") if ev else None
        except Exception:
            total, last_at = -1, None
        snapshot[wid] = {"total": total, "last_at": last_at, "status": status}
        last_dt = parse_dt(last_at)
        age = (nowt - last_dt).total_seconds() if last_dt else None
        prev_total = prev.get(wid, {}).get("total")
        advanced = prev_total is not None and total != prev_total
        flag = ""
        if age is not None and age > STALL_SECS:
            flag = f"  *** STALLED (no event {int(age)}s) ***"
            alerts.append(f"STALLED {short} (quiet {int(age)}s)")
        elif age is not None and age > SLOW_SECS and not advanced and prev_total is not None:
            flag = f"  ~ slow (quiet {int(age)}s, no progress since last tick)"
            alerts.append(f"slow {short} (quiet {int(age)}s, no progress)")
        delta = f"+{total - prev_total}" if isinstance(prev_total, int) and prev_total >= 0 else ""
        agestr = f"{int(age)}s" if age is not None else "?"
        report.append(f"  {status:8} total={total:<6}{delta:<6} last={agestr:>6} ago  {short}{flag}")

    json.dump(snapshot, open(STATE, "w"), indent=2)
    return counts, report, snapshot, alerts


def live_set(snapshot):
    return frozenset(k for k, v in snapshot.items() if v.get("status") in LIVE)


def print_tick(counts, report):
    summary = " ".join(f"{k}={v}" for k, v in sorted(counts.items()))
    print(f"=== refactor-guide monitor {now().isoformat(timespec='seconds')} | {summary} ===")
    for line in report:
        print(line)
    if not report:
        print("  no live workstreams (all terminal or planning/queued)")


def main():
    counts, report, _snap, _alerts = tick(load_prev())
    print_tick(counts, report)


def watch(interval=150, heartbeat=600):
    """Loop forever; emit a line only on alert, on a change in the live-
    workstream set (stage transition / completion), or a heartbeat."""
    prev_live = None
    prev_counts = None
    last_beat = 0.0
    while True:
        try:
            prev = load_prev()
            counts, report, snapshot, alerts = tick(prev)
        except Exception as e:
            print(f"[monitor-error] {e}", flush=True)
            time.sleep(interval)
            continue
        cur_live = live_set(snapshot)
        ev = now().isoformat(timespec="seconds")
        summary = " ".join(f"{k}={v}" for k, v in sorted(counts.items()))
        for a in alerts:
            print(f"[ALERT {ev}] {a}", flush=True)
        if prev_live is not None and cur_live != prev_live:
            done = prev_live - cur_live
            new = cur_live - prev_live
            parts = []
            if done:
                parts.append("completed/left: " + ", ".join(sorted(d.split('phase-')[-1] for d in done)))
            if new:
                parts.append("now live: " + ", ".join(sorted(n.split('phase-')[-1] for n in new)))
            print(f"[PROGRESS {ev}] {summary} | " + " ; ".join(parts), flush=True)
        elif prev_counts is not None and counts != prev_counts:
            print(f"[PROGRESS {ev}] {summary}", flush=True)
        beat = time.time()
        if beat - last_beat >= heartbeat:
            live_desc = ", ".join(sorted(s.split('phase-')[-1] for s in cur_live)) or "(none live)"
            print(f"[heartbeat {ev}] {summary} | live: {live_desc}", flush=True)
            last_beat = beat
        prev_live, prev_counts = cur_live, counts
        time.sleep(interval)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--watch":
        watch()
    else:
        main()
