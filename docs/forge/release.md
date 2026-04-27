# Forge — release checklist (v0.1.0)

## Pre-cut gates

Tick every box. If anything is red, fix it or carry it forward to
`forge_v2.md` — do not ship around it.

### Build & test

- [ ] `dotnet build src/forge/Forge.Api/Forge.Api.csproj` — 0 warnings, 0 errors
- [ ] `dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj` — all green
- [ ] `cd src/forge/Forge.UI && npx tsc --noEmit` — clean
- [ ] `cd src/forge/Forge.UI && npx vite build` — clean
- [ ] `cd src/forge/Forge.UI.Tests && npx playwright test` — all green
       (smoke + DnD + write-roundtrip + audit-smoke + crew-avatars + drag-overlay)

### Phase 0–4 fidelity

- [ ] Plan board screenshot diff against `docs/examples/forge-board.html` is 0
- [ ] Timeline screenshot diff (Day / Week / Sprint, light + dark) is 0
- [ ] Workstreams screenshot diff (stats + grid + new-ws modal) is 0
- [ ] PR view screenshot diff (every card variant from the seed) is 0

### Phase 5 — providers (read)

- [ ] Boot against a real ADO project: cards land in `/api/cards`, PRs in `/api/prs`
- [ ] Boot against a real GitHub repo: same
- [ ] `/settings` shows both providers `connected` after "Test connection"

### Phase 6 — providers (write)

- [ ] Create a card with `Post to ADO` set — work item appears in ADO
- [ ] Create a card with `Post to GitHub` set — issue appears in GitHub
- [ ] Open a PR from a workstream → real PR exists, body links the seed cards
- [ ] Approve / Request changes / Comment from `/prs` — votes show on the
      remote PR
- [ ] Auto-merge: arm a green PR with no human gate → `AutoMergeWatcher`
      merges within one tick
- [ ] Audit log at `/audit` lists every write with the right user
- [ ] `Idempotency-Key` header on a duplicate write returns the cached body
      and `Idempotent-Replay: true`

### Phase 7 — polish

- [ ] `Cmd/Ctrl-K` palette opens, jumps to view / card / PR / workstream
- [ ] `?` shows the keyboard map
- [ ] Empty states render on board / prs / workstreams when their data is empty
- [ ] Throwing inside a view is caught by the boundary; "Copy report" works
- [ ] `Forge:Auth:Secret` set → unauthenticated writes return 401
- [ ] `docker compose up --build` brings the stack up; `http://localhost:8080`
      serves the UI and proxies `/api/*` to the api container

### Docs

- [ ] `docs/forge/setup.md` walks a clean machine from zero to dashboard
- [ ] `docs/forge/providers.md` matches the actual config keys in `Program.cs`
- [ ] `docs/forge/keyboard.md` matches the live shortcuts
- [ ] `forge_v2.md` exists and lists every dropped item with a one-line why

## Cut steps

1. Bump the API + UI versions:
   - `src/forge/Forge.Api/Forge.Api.csproj` `<Version>0.1.0</Version>`
   - `src/forge/Forge.UI/package.json` `"version": "0.1.0"`
2. Tag: `git tag -a v0.1.0 -m "Forge 0.1.0 — first usable release"`
3. Push the tag (`git push --tags`).
4. Build + push the docker images (optional):
   ```pwsh
   docker compose build
   docker tag forge-api:local your-registry/forge-api:0.1.0
   docker tag forge-ui:local  your-registry/forge-ui:0.1.0
   docker push your-registry/forge-api:0.1.0
   docker push your-registry/forge-ui:0.1.0
   ```
5. Open the release notes (next section).

## Release notes template

```
## Forge 0.1.0

First usable release. The dashboard renders four primary views (Plan,
Timeline, Workstreams, PRs) plus Overview and Audit, with bidirectional
sync against ADO and GitHub.

### Highlights
- Plan & Design board with optimistic drag-drop and a live drag overlay
- Timeline with Day / Week / Sprint zoom and forecast overlay
- Workstreams: branch + worktree + agents + linked PRs
- PR board with build-status streaming, auto-merge, human-gate enforcement
- Provider write surface (cards, PRs, comments, reviews, pipelines)
- Audit log + idempotency keys + per-user PAT support
- Cmd/Ctrl-K command palette, ? keyboard map, error boundaries with
  copy-report

### Known limits (rolled to v2)
See `docs/forge_v2.md`.
```

## Post-cut

- Update `docs/forge_implementation.md`: mark all Phase-7 boxes `[x]`.
- Open the v0.1.1 milestone for follow-ups.
