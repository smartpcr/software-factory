# Forge — local setup

A 5-minute walkthrough to get the dashboard running on your laptop.

## Prerequisites

- .NET SDK 8.0+ (`dotnet --version` ≥ 8.0)
- Node 20 LTS (`node --version` ≥ 20)
- Git (worktree support — Git 2.5+)

## First run

```pwsh
# from repo root
dotnet build src/forge/Forge.Api/Forge.Api.csproj
cd src/forge/Forge.UI && npm ci
```

In two terminals:

```pwsh
# terminal 1 — API on :5180
dotnet run --project src/forge/Forge.Api/Forge.Api.csproj

# terminal 2 — UI dev server on :5181
cd src/forge/Forge.UI && npm run dev
```

Open `http://localhost:5181/`. The Overview lights up with the seed data.
The first request creates `forge.db` next to the API binary.

## Without the API

If the API is offline or you want to demo the UI standalone, append
`?seed=local` to any URL — the UI falls back to the bundled sample data.

## Configuration

All server settings live under the `Forge:*` section:

| Key | Purpose |
|-----|---------|
| `Forge:Db:Path` | SQLite file location (default `forge.db`) |
| `Forge:Auth:Mode` | `single-user` (default) or `azure-ad` |
| `Forge:Auth:Secret` | Shared secret for `X-Forge-Secret` writes |
| `Forge:Sync:Enabled` | Background provider sync on/off |
| `Forge:Sync:IntervalSeconds` | Poll cadence (jittered) |
| `Forge:HumanGate:*` | Branch / label rules that block auto-merge |

In dev, set them through user-secrets:

```pwsh
cd src/forge/Forge.Api
dotnet user-secrets set "Forge:Auth:Secret" "$(openssl rand -hex 16)"
```

In Docker, copy `forge.env.sample` to `forge.env` and edit. See
[providers.md](providers.md) for ADO + GitHub credentials.

## Tests

```pwsh
# .NET integration / unit tests
dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj

# Playwright smoke + fidelity
cd src/forge/Forge.UI.Tests && npm ci && npx playwright test
```

## Troubleshooting

- **Port collision** — `:5180` or `:5181` already taken? Override via
  `--urls http://localhost:6180` on the API and `vite --port 6181` on the UI,
  then set `VITE_FORGE_API_URL=http://localhost:6180`.
- **Stale DB** — delete `forge.db` and re-launch; the seed re-runs on first boot.
- **Hot reload "stuck"** — a runtime React error can leave HMR in a corrupt
  state. Hard-refresh the tab.
