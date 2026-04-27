# Forge — Design tokens

> Inventory of CSS custom properties consumed by Forge UI surfaces. Tokens are defined in `src/forge/Forge.UI/src/styles/theme.css` and swap on `[data-theme="dark"]`. Phase R will extract these into a generated layer (Style Dictionary or similar) — until then this doc is the authoritative list.

## Phase 11 — agent runtime surfaces

The agent surfaces (`/crews`, the TopBar salary chip, the soul.md editor entry, agent detail panel) are exempt from the `forge-board.html` screenshot-diff fidelity check (per the Phase 11.12 note in `forge_implementation.md`) but are required to use **only existing tokens**. No new tokens were introduced.

| Token | Used by | Purpose |
|-------|---------|---------|
| `--accent` | TopBar salary chip dot, `/crews` model pill border + label, sparkline stroke | Primary accent — agent UI tints |
| `--accent-soft` | Model pill background, salary chip dot halo | Tinted accent surface |
| `--text-primary` | Salary figures (`$X.XX`) | Body text |
| `--text-secondary` | Pricing rows, salary chip "today" label | Subdued body text |
| `--text-tertiary` | Empty-state copy ("—" salary placeholder), spark caption | De-emphasised text |
| `--border-strong` | Agent chip border (non-lead), salary chip border | Stroke |
| `--bg-elevated` | Salary chip background | Card / panel surface |
| `--font-mono` | Salary figures, pricing rows, agent chip ids | Numeric / id columns |
| `--danger` | Salary "today" figure when over `dailyBudgetUsd` | Budget breach |

## Phase 16 — Live Execution view

The Live Execution view at `/work-items/{id}/live`, the workstream "live" badge, and the Swarm view's pair lane are exempt from the `forge-board.html` screenshot-diff (Phase 16.17). They reuse the Phase 11 / Phase 14 palette plus exactly **one** new token:

| Token | Used by | Purpose |
|-------|---------|---------|
| `--graph-line` | `live-exec__line::before` rule, transcript row separators | Subtle vertical accent rail next to each transcript event when the side (generator / evaluator) isn't otherwise distinguished |

## Verification

A token grep across the agent UI files should not surface anything that isn't in the table above:

```bash
# from repo root
grep -rEho 'var\(--[a-z0-9-]+(?:, [^\)]+)?\)' \
  src/forge/Forge.UI/src/views/CrewsView.tsx \
  src/forge/Forge.UI/src/shell/TopBar.tsx \
  | sort -u
```

If a new token surfaces here, add it to this doc with the same row format before merging.

## Cross-references

- Source: `src/forge/Forge.UI/src/styles/theme.css`
- Spec: `docs/forge_implementation.md` — Phase 11.12 fidelity exemption note
- Related: Phase 8.13, 10.12 (other view exemptions that share the rule "exempt from screenshot diff but must use existing tokens").
