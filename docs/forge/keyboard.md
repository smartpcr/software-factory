# Forge — keyboard reference

Shortcuts work from any view (unless noted). Press `?` in-app to bring up
the same map as a modal.

| Keys | Action |
|------|--------|
| `?` | Open the keyboard-map overlay |
| `Cmd/Ctrl-K` | Open the command palette |
| `Esc` | Close the active panel / modal |
| `n` | New card (only on `/plan`) |
| `/` | Focus the search input in the filter bar |
| `1` … `5` | Scroll the focus to the n-th column on `/plan` |

## Command palette tips

- Type a partial card / PR / workstream id — `234`, `oidc`, `pr 240`.
- Type `go` to surface every "Go to …" jump.
- Type `theme`, `reload`, or `report` to surface global actions.
- Arrow keys move the highlight, `Enter` runs the highlighted command,
  `Esc` closes without running.

## Drag-drop on the board

- Hold pointer on a card and move 4 px to start the drag (a faint clone
  follows the cursor).
- Drop targets light up green for allowed moves, amber for moves that
  need confirmation (skip-phase / re-open done), red for blocked moves
  (different epic, policy reject).
- The optimistic UI moves the card immediately; if the server rejects the
  move it snaps back and a toast explains why.
