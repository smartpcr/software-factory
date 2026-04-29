"""Phase 23/24 walk-through: open repo edit modal, set area paths, open
sync history drawer, hit Sync now, screenshot each step."""
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

    # 1. Land on /products
    page.goto("http://localhost:5181/products", wait_until="networkidle")
    page.wait_for_load_state("networkidle")
    shot(page, "01-products-initial")

    # 2. Click the edit (pencil) icon for atlas-platform.
    edit_btn = page.locator('button[aria-label="Edit atlas-platform"]')
    edit_btn.wait_for(state="visible", timeout=5000)
    edit_btn.click()
    page.wait_for_selector('[data-testid="repo-area-paths"]', timeout=5000)
    page.wait_for_timeout(300)  # let modal animate
    shot(page, "02-edit-modal-with-area-paths")

    # 3. Fill in two area paths + agent-ready team.
    page.locator('[data-testid="repo-area-paths"]').fill(
        "Atlas\\Platform\nAtlas\\Edge\\IoT"
    )
    page.locator('[data-testid="repo-agent-ready-team"]').fill("Engineering")
    shot(page, "03-edit-modal-filled")

    # 4. Save. Save may show a clone error if the local path doesn't exist —
    # we capture both states.
    page.get_by_role("button", name="Save changes").click()
    page.wait_for_timeout(2500)
    shot(page, "04-after-save")
    # Force-close in case the modal stuck on an error (real save still
    # persisted area paths server-side).
    if page.locator('[data-testid="repo-area-paths"]').count() > 0:
        page.keyboard.press("Escape")
        page.wait_for_timeout(400)
        # If Escape didn't take it (busy state), click the Cancel button.
        if page.locator('[data-testid="repo-area-paths"]').count() > 0:
            try:
                page.get_by_role("button", name="Cancel").click()
                page.wait_for_timeout(400)
            except Exception:
                pass
    shot(page, "04b-modal-dismissed")

    # 5. Open the sync drawer by clicking the status strip.
    strip = page.locator('[data-testid="repo-sync-strip-atlas-platform"]')
    strip.wait_for(state="visible", timeout=5000)
    strip.click()
    page.wait_for_selector('[data-testid="repo-sync-drawer-atlas-platform"]',
                           timeout=5000)
    page.wait_for_timeout(500)
    shot(page, "05-sync-drawer-open")

    # 6. Click "Sync now" — exercise the failure / runs path.
    page.get_by_test_id("sync-now-button").click()
    page.wait_for_timeout(2500)  # give the request a moment
    shot(page, "06-sync-now-clicked")

    # 7. Close the drawer + final products view (verifies positioning + close).
    page.keyboard.press("Escape")
    page.wait_for_timeout(400)
    shot(page, "07-drawer-closed")

    browser.close()
print("DONE")
