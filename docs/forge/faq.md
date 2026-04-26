# Forge — FAQ

### Why does the board snap back when I drag?

Two possibilities:

1. **The server rejected the move.** Open the toast and check the audit log
   at `/audit` — you'll see a `409` or `422` row. Usually a policy rule
   (cards stay in their epic; can't skip phases without confirmation).
2. **The API is unreachable.** The store now keeps the optimistic move and
   shows a `(offline)` toast instead of rolling back, but very old builds
   used to roll back. Hard-refresh if you see the rollback behaviour.

### Where is the data stored?

A single SQLite file. Path defaults to `forge.db` next to the API binary,
overridable via `Forge:Db:Path`. In Docker the file lives on the
`forge-data` named volume.

### How do I clear and reseed?

Delete `forge.db`. The next API boot recreates the schema and re-runs the
seed (idempotent).

### Why don't I see my real ADO/GitHub data?

Three checks, in order:

1. `/settings` → both providers should report `connected`. Click "Test
   connection" if the row is grey.
2. `Forge:Sync:Enabled` is `true` and the API has been running long enough
   for one tick (`Forge:Sync:IntervalSeconds`).
3. The token has the right scopes — see [providers.md](providers.md).

### How do I avoid storing PATs in plain text?

Use `dotnet user-secrets` in dev. In Docker, mount `forge.env` as a
read-only secret volume. A future iteration will encrypt secrets at rest in
SQLite (tracked in `forge_v2.md`).

### Why do PR review buttons return 502?

The endpoint forwards to the configured provider. A 502 means the provider
itself returned an error or rate-limited. Check `/api/providers` for the
last error count and rate-limit budget.

### What does "human gate" mean?

Server-side rules (paths / branch names / labels) that mark a PR as
"requires human approval". Auto-merge skips gated PRs even if CI is green.
Configure via `Forge:HumanGate:*` — see the sample env.

### Can I run only the UI for a demo?

Yes. `cd src/forge/Forge.UI && npm run dev`, then visit
`http://localhost:5181/?seed=local`. The UI uses the bundled sample data
when the API is unreachable.

### How do I add a new agent to the crew?

Append to `overviewAgents` in `src/forge/Forge.UI/src/crew.ts` with a role
+ capabilities + image import from `@/assets/crews/`. The Overview page
picks it up on the next render.
