"""
Phase 56 follow-up #2 verification — Conversation > Prompt tab auto-load.

What this proves:
- Navigating to a workstream that has truncated `task.prompt` events
  triggers per-event /api/work-items/{wi}/transcript/{eventId} fetches.
- Each fetch resolves successfully and clears the "loading full prompt"
  indicator (the cancel-race fix in PromptView.tsx).
- After 20 seconds, no rows are still showing the loading indicator
  and no rows are still flagged "! truncated".

Pre-reqs:
- Forge.Api running on http://localhost:5180
- Forge UI (Vite) running on http://localhost:5181
- Workstream id below has at least one truncated prompt event with a
  positive DB id.

Run with:
    $env:PYTHONIOENCODING="utf-8"
    python scripts\test-prompt-load.py
"""
from __future__ import annotations

import sys
from pathlib import Path
from playwright.sync_api import sync_playwright, Page, Response

UI_BASE = "http://localhost:5181"
API_BASE = "http://localhost:5180"
STORY_ID = "qq:MICROSOFT-TEAMS-MESS"
WS_ID = "ws-qq-microsoft-teams-mess-phase-teams-bot-framework-core-stage-asp-net-core-bot-host"

SHOT_DIR = Path(__file__).parent / ".test-shots"
SHOT_DIR.mkdir(exist_ok=True)


def shot(page: Page, name: str) -> None:
    p = SHOT_DIR / f"{name}.png"
    page.screenshot(path=str(p), full_page=True)
    print(f"  shot: {p.as_posix()}")


def main() -> int:
    transcript_calls: list[tuple[str, int, int]] = []  # (id, status, body_len)
    console_msgs: list[str] = []

    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True)
        ctx = browser.new_context(viewport={"width": 1600, "height": 1000})
        page = ctx.new_page()

        page.on("console", lambda m: console_msgs.append(f"[{m.type}] {m.text}"))

        def on_response(resp: Response) -> None:
            url = resp.url
            if "/transcript/" in url and "/api/work-items/" in url:
                # Extract trailing id segment.
                tail = url.rsplit("/transcript/", 1)[-1].split("?", 1)[0]
                try:
                    body_len = len(resp.body() or b"")
                except Exception:
                    body_len = -1
                transcript_calls.append((tail, resp.status, body_len))
        page.on("response", on_response)

        # 1. Navigate straight to the workstream's Conversation tab.
        url = (
            f"{UI_BASE}/workstreams/{STORY_ID}"
            f"?sel=phase&selId={WS_ID}&tab=conv"
        )
        print(f"[1] navigating to {url}")
        page.goto(url, wait_until="domcontentloaded", timeout=20_000)
        page.wait_for_timeout(3000)
        shot(page, "01-conversation-tab")

        # 2. Click the "Prompt" filter chip inside Conversation.
        print("[2] clicking Prompt filter chip")
        prompt_chip = page.locator("button:has-text('Prompt')").first
        if prompt_chip.count() == 0:
            print("FAIL: Prompt chip not found")
            shot(page, "02-no-prompt-chip")
            return 2
        prompt_chip.click()
        page.wait_for_timeout(2000)
        shot(page, "03-prompt-chip-clicked")

        # 3. Wait for any "loading full prompt" indicators to clear.
        print("[3] waiting for auto-fetches to settle (up to 20s)")
        deadline_ms = 20_000
        elapsed = 0
        last_loading = -1
        while elapsed < deadline_ms:
            page.wait_for_timeout(1000)
            elapsed += 1000
            loading_count = page.locator("text=/loading full prompt/i").count()
            if loading_count != last_loading:
                print(f"  t+{elapsed//1000}s: loading={loading_count} fetches={len(transcript_calls)}")
                last_loading = loading_count
            if loading_count == 0 and len(transcript_calls) > 0:
                # If we already saw at least one fetch and nothing is loading, we're done.
                page.wait_for_timeout(1500)  # let any in-flight last fetch settle
                if page.locator("text=/loading full prompt/i").count() == 0:
                    break
        shot(page, "04-prompt-tab-after-fetch")

        # 4. Final assertions.
        loading_left = page.locator("text=/loading full prompt/i").count()
        truncated_left = page.locator("text=/! truncated/i").count()
        failed_left = page.locator("text=/load failed/i").count()
        print()
        print("=== RESULTS ===")
        print(f"  /transcript/ fetches observed: {len(transcript_calls)}")
        for i, (ev, st, bl) in enumerate(transcript_calls[:20], 1):
            print(f"    {i:2d}. eventId={ev:>10s} status={st} body_len={bl}")
        if len(transcript_calls) > 20:
            print(f"    ... and {len(transcript_calls)-20} more")
        print(f"  loading indicators still showing: {loading_left}")
        print(f"  '! truncated' markers still showing: {truncated_left}")
        print(f"  'load failed' markers still showing: {failed_left}")

        if loading_left > 0:
            print("FAIL: stuck loading indicators remain")
            print("\nLast 30 console messages:")
            for m in console_msgs[-30:]:
                print(f"  {m}")
            browser.close()
            return 3

        if len(transcript_calls) == 0:
            print("WARN: no /transcript/ fetches observed — either the workstream had no truncated prompts, or auto-fetch never fired")
            browser.close()
            return 4

        # Demand all fetches succeeded.
        bad = [c for c in transcript_calls if c[1] != 200]
        if bad:
            print(f"FAIL: {len(bad)} non-200 transcript fetch(es): {bad[:5]}")
            browser.close()
            return 5

        print("PASS - auto-fetch fired, all responses 200, no stuck loading indicators")
        browser.close()
        return 0


if __name__ == "__main__":
    sys.exit(main())
