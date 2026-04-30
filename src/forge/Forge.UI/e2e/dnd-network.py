"""Drag a card and capture the /move API call to see if dnd actually fires."""
from playwright.sync_api import sync_playwright
from pathlib import Path

OUT = Path(r"C:\Users\xiaodoli\Downloads\sync-walkthrough")


def shot(page, name):
    p = OUT / f"{name}.png"
    page.screenshot(path=str(p), full_page=False)
    print(f"WROTE {p}")


with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    ctx = browser.new_context(viewport={"width": 1600, "height": 1000})
    page = ctx.new_page()

    move_calls = []
    page.on("request", lambda r: move_calls.append(("REQ", r.method, r.url, r.post_data)) if "/move" in r.url else None)
    page.on("requestfinished", lambda r: move_calls.append(("OK", r.status if hasattr(r,'status') else '?', r.url)) if "/move" in r.url else None)
    page.on("response", lambda r: move_calls.append(("RESP", r.status, r.url)) if "/move" in r.url else None)

    page.goto("http://localhost:5181/work-items", wait_until="networkidle", timeout=30000)
    page.wait_for_timeout(2000)

    # Force board mode via localStorage
    page.evaluate("localStorage.setItem('forge.wit.mode','board')")
    page.reload(wait_until="networkidle")
    page.wait_for_timeout(2000)

    # Find a card in Proposed and drag it to Active.
    proposed_col = page.locator("section[aria-label='Proposed']").first
    active_col = page.locator("section[aria-label='Active']").first

    cards_in_proposed = proposed_col.locator(".wit-board-card-id").all()
    print(f"cards in Proposed: {len(cards_in_proposed)}")
    if not cards_in_proposed:
        print("no cards to drag")
        browser.close()
        raise SystemExit

    # Drag the first one
    first_id = cards_in_proposed[0].text_content()
    print(f"dragging: {first_id}")

    # Use the wrapping article (the .wit-board-card) for the drag handle.
    card = proposed_col.locator(".wit-board-card").first
    cb = card.bounding_box()
    tb = active_col.bounding_box()
    print(f"card box: x={cb['x']:.0f} y={cb['y']:.0f} w={cb['width']:.0f} h={cb['height']:.0f}")
    print(f"active col box: x={tb['x']:.0f} y={tb['y']:.0f} w={tb['width']:.0f} h={tb['height']:.0f}")

    sx = cb['x'] + cb['width']/2
    sy = cb['y'] + cb['height']/2
    tx = tb['x'] + tb['width']/2
    ty = max(tb['y'] + 200, 200)

    # Slow, deliberate drag.
    page.mouse.move(sx, sy)
    page.mouse.down()
    page.wait_for_timeout(120)
    # Step the cursor in 12 increments to make sure activationConstraint trips.
    for i in range(1, 13):
        x = sx + (tx - sx) * i / 12
        y = sy + (ty - sy) * i / 12
        page.mouse.move(x, y, steps=4)
        page.wait_for_timeout(60)
    page.wait_for_timeout(300)
    shot(page, "30-mid-drag-deliberate")
    page.mouse.up()
    page.wait_for_timeout(2500)
    shot(page, "31-after-drop-deliberate")

    print("\nnetwork events for /move:")
    for ev in move_calls:
        print(f"  {ev}")

    browser.close()
print("DONE")
