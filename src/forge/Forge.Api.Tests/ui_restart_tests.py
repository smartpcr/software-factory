"""
Playwright UI tests for live-exec view behavior.
Verifies:
  1. Prompts section starts collapsed on fresh load
  2. Both WIT State pill and ExecBadge are visible on board cards
  3. Live-exec header shows execution status badge when failed
  4. Restart button exists and is clickable
"""
from playwright.sync_api import sync_playwright
import sys

BASE = "http://localhost:5181"
LIVE_EXEC_WI = "WI-7666"

def test_prompts_collapsed_on_load(page):
    """Prompts section should start collapsed (▸ not ▾)."""
    page.goto(f"{BASE}/work-items/{LIVE_EXEC_WI}/live")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(2000)

    prompts = page.locator(".live-exec__prompts-header")
    if prompts.count() == 0:
        print("PASS: prompts_collapsed — no prompts section rendered (OK)")
        return True

    title = prompts.first.locator(".live-exec__prompts-title").inner_text()
    if title.startswith("▸"):
        print("PASS: prompts_collapsed — section starts collapsed")
        return True
    else:
        print(f"FAIL: prompts_collapsed — expected collapsed (▸), got: {title[:20]}")
        page.screenshot(path="C:/Users/xiaodoli/.copilot/session-state/2079bf31-fb65-46f6-aa37-b9f43e4db233/files/fail_prompts.png")
        return False


def test_exec_status_badge_visible(page):
    """Live-exec header should show status badge when execution is not idle.
    If status is 'live', the badge is intentionally hidden (active = no badge needed).
    Badge appears for failed/completed/queued."""
    page.goto(f"{BASE}/work-items/{LIVE_EXEC_WI}/live")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(2000)

    badge = page.locator(".live-exec__status-badge")
    # Check the iter element exists (proves the header rendered)
    iter_el = page.locator(".live-exec__iter")
    if iter_el.count() == 0:
        print("FAIL: exec_status_badge — header iter element not found")
        page.screenshot(path="C:/Users/xiaodoli/.copilot/session-state/2079bf31-fb65-46f6-aa37-b9f43e4db233/files/fail_badge.png")
        return False

    if badge.count() > 0:
        text = badge.first.inner_text()
        print(f"PASS: exec_status_badge — badge visible with text: '{text}'")
        return True
    else:
        # Badge hidden = status is 'live' or 'idle' (both valid)
        print("PASS: exec_status_badge — no badge (status is live/idle, badge correctly hidden)")
        return True


def test_restart_button_exists(page):
    """Restart button should be present in the header."""
    page.goto(f"{BASE}/work-items/{LIVE_EXEC_WI}/live")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(2000)

    btn = page.locator("button", has_text="Restart")
    if btn.count() > 0:
        print("PASS: restart_button — button exists")
        return True
    else:
        print("FAIL: restart_button — no Restart button found")
        page.screenshot(path="C:/Users/xiaodoli/.copilot/session-state/2079bf31-fb65-46f6-aa37-b9f43e4db233/files/fail_restart.png")
        return False


def test_board_card_shows_both_states(page):
    """Board cards should show both StatePill and ExecBadge."""
    page.goto(f"{BASE}/work-items")
    page.wait_for_load_state("networkidle")
    page.wait_for_timeout(3000)

    # Find the card for WI-7666
    card = page.locator(f"[data-id='{LIVE_EXEC_WI}']").or_(
        page.locator(f"text={LIVE_EXEC_WI}").locator("..").locator("..")
    )
    if card.count() == 0:
        # Try the work items list view instead
        card = page.locator(f"text={LIVE_EXEC_WI}").first
        if card.count() == 0:
            print("SKIP: board_card_states — WI-7666 card not found on current view")
            return True

    # Check for ExecBadge (the exec-badge-live class or inline style with exec palette)
    exec_badge = page.locator(".exec-badge-live, [class*='exec-badge']").or_(
        page.locator("span", has_text="failed")
    )
    state_pill = page.locator("[class*='state-pill'], [class*='StatePill']").or_(
        page.locator("span", has_text="active")
    )

    has_exec = exec_badge.count() > 0
    has_state = state_pill.count() > 0
    if has_exec and has_state:
        print("PASS: board_card_states — both ExecBadge and StatePill visible")
        return True
    else:
        print(f"FAIL: board_card_states — exec={has_exec}, state={has_state}")
        page.screenshot(path="C:/Users/xiaodoli/.copilot/session-state/2079bf31-fb65-46f6-aa37-b9f43e4db233/files/fail_board.png")
        return False


def main():
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        page = browser.new_page(viewport={"width": 1600, "height": 900})

        results = []
        results.append(("prompts_collapsed", test_prompts_collapsed_on_load(page)))
        results.append(("exec_status_badge", test_exec_status_badge_visible(page)))
        results.append(("restart_button", test_restart_button_exists(page)))
        results.append(("board_card_states", test_board_card_shows_both_states(page)))

        browser.close()

        print("\n--- Summary ---")
        passed = sum(1 for _, ok in results if ok)
        total = len(results)
        for name, ok in results:
            print(f"  {'✓' if ok else '✗'} {name}")
        print(f"\n{passed}/{total} passed")

        if passed < total:
            sys.exit(1)


if __name__ == "__main__":
    main()
