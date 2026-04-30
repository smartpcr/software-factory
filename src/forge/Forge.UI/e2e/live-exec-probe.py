"""Probe the live-exec page that the user said is stuck."""
from playwright.sync_api import sync_playwright
from pathlib import Path

OUT = Path(r"C:\Users\xiaodoli\Downloads\sync-walkthrough")
OUT.mkdir(parents=True, exist_ok=True)


def shot(page, name):
    p = OUT / f"{name}.png"
    page.screenshot(path=str(p), full_page=True)
    print(f"WROTE {p}")


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(viewport={"width": 1600, "height": 1000})
    page = ctx.new_page()

    console_log = []
    page.on("console", lambda m: console_log.append(f"[{m.type}] {m.text[:200]}"))
    page.on("pageerror", lambda e: console_log.append(f"[ERROR] {str(e)[:300]}"))

    pending = []
    page.on("request", lambda r: pending.append(r.url) if "/api/" in r.url else None)
    finished = []
    page.on("requestfinished", lambda r: finished.append((r.url, r.timing)) if "/api/" in r.url else None)

    print(">> navigating")
    try:
        page.goto("http://localhost:5181/work-items/WI-37736240/live", timeout=25000)
    except Exception as e:
        print(f"goto error: {e}")

    page.wait_for_timeout(8000)
    shot(page, "10-live-exec-after-8s")

    # Try waiting longer
    page.wait_for_timeout(10000)
    shot(page, "11-live-exec-after-18s")

    # Show in-flight requests
    in_flight = set(pending) - {url for url, _ in finished}
    print(f"\nin-flight after 18s: {len(in_flight)}")
    for u in sorted(in_flight):
        print(f"  STILL PENDING: {u}")
    print(f"\nfinished:")
    for u, _ in finished[-15:]:
        print(f"  ok: {u}")

    print(f"\nconsole tail:")
    for line in console_log[-12:]:
        print(f"  {line}")

    browser.close()
print("DONE")
