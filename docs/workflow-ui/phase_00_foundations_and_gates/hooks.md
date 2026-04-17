# Pre-commit Hooks (local development)

## Install

After cloning and running `npm ci` in `src/WorkflowUI/`, install the pre-commit hook:

```bash
npm --prefix src/WorkflowUI run prepare
```

This wires up **lefthook** hooks that mirror CI gates locally:

- **client-eslint** — runs ESLint on staged `.ts`/`.tsx` files.
- **client-prettier** — runs Prettier format check on staged client files.
- **dotnet-format** — runs `dotnet format --verify-no-changes` on staged `.cs` files.

## Escape hatch

```bash
git commit --no-verify -m "skip hooks"
```

CI still enforces all gates, so `--no-verify` is safe for emergencies only.

## Notes

- Hooks run only on staged files (`{staged_files}`) — not the whole tree.
- CI mirrors these checks but does NOT depend on hooks being installed.
- Windows (`pwsh.exe`) and Unix-like shells (bash) both work — lefthook auto-detects.
