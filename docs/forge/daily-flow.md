# Forge — daily flow

A typical sprint day, mapped to the views.

## Morning (10 minutes)

1. Open `/` (Overview). Skim the activity feed and the agent crew —
   is anyone idle who shouldn't be? Any failing CI in the stat strip?
2. Hop to `/plan`. The sprint pill shows real burn-down (`done / total pts`).
   Filter by **Needs my approval** to clear the human gate first.
3. For anything sitting in **Build**, click into the card and read the
   agent reasoning trace. Drag forward to **Review** if the build is green.

## Midday (working)

- Press `Cmd/Ctrl-K` to jump anywhere by typing — view name, card id,
  workstream name. Try `nz` → finds Ne Zha's open card; `pr 234` → opens
  PR #234's detail panel.
- On `/workstreams`, hit **PR** on a card to open a PR through the configured
  provider. The PR templated body links every card the workstream is solving.
- Hit `n` on `/plan` to draft a new card. Pick a provider in the modal to
  push it to ADO/GitHub at the same time.

## End-of-day

- `/prs` → filter **Failing** to triage red builds. Re-run / cancel from
  the detail panel; the build log streams in via SignalR.
- `/audit` → confirm every write you made shows up. Each row carries the
  `X-Forge-User`, the `Idempotency-Key` (if any), and a 256-char body snippet.
- Toggle auto-merge on anything that's only waiting for CI. The
  `AutoMergeWatcher` ticks every `Forge:Sync:IntervalSeconds` and merges as
  soon as build = passing + review = approved + no human gate.

## Useful loops

- `Shift-?` opens the keyboard map.
- Toggle theme from the topbar or via the palette → **Toggle theme**.
- "Report a problem" (palette → `report`) copies the last 50 actions, the
  current URL, and a correlation id to your clipboard — paste into the bug.
