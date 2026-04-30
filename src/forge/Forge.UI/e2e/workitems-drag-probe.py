"""Probe: can we drag WI-37736240 between columns on the work-items view?"""
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
    console = []
    page.on("console", lambda m: console.append(f"[{m.type}] {m.text[:200]}"))
    page.on("pageerror", lambda e: console.append(f"[ERR] {str(e)[:300]}"))

    print(">> nav work-items")
    page.goto("http://localhost:5181/work-items", wait_until="networkidle", timeout=30000)
    page.wait_for_timeout(2000)
    shot(page, "20-workitems-default")

    # See current mode by checking the active toggle.
    active_modes = page.locator(".wit-mode.active, [aria-pressed='true']").all_text_contents()
    print(f"active modes: {active_modes}")

    # Click Board mode toggle.
    try:
        page.locator("text=Board").first.click(timeout=2000)
        page.wait_for_timeout(800)
        print("clicked Board (text)")
    except Exception as e2:
        print(f"could not click Board: {e2}")
    shot(page, "21-workitems-board")

    # Locate the WI-37736240 card.
    card = page.locator("[title='WI-37736240']").first
    try:
        card.scroll_into_view_if_needed(timeout=3000)
    except Exception as e:
        print(f"WI-37736240 not visible: {e}")
        # Print what cards are visible
        ids = page.locator(".wit-board-card-id").all_text_contents()
        print(f"visible card ids ({len(ids)}): {ids[:15]}")

    # Find a target drop column.
    col = page.locator("section[aria-label='Active']").first
    try:
        cb = card.bounding_box()
        tb = col.bounding_box()
        print(f"card box: {cb}")
        print(f"col box:  {tb}")
        if cb and tb:
            page.mouse.move(cb["x"] + cb["width"]/2, cb["y"] + cb["height"]/2)
            page.mouse.down()
            page.wait_for_timeout(150)
            # move in two steps so the activationConstraint(distance:4) trips
            page.mouse.move(cb["x"] + cb["width"]/2 + 10, cb["y"] + cb["height"]/2 + 10, steps=5)
            page.wait_for_timeout(150)
            page.mouse.move(tb["x"] + tb["width"]/2, tb["y"] + tb["height"]/2, steps=20)
            page.wait_for_timeout(300)
            shot(page, "22-during-drag")
            page.mouse.up()
            page.wait_for_timeout(1500)
            shot(page, "23-after-drop")
    except Exception as e:
        print(f"drag failed: {e}")

    print("\nconsole tail:")
    for line in console[-15:]:
        try:
            print(f"  {line}")
        except UnicodeEncodeError:
            print(f"  [non-ascii]")

    browser.close()
print("DONE")
