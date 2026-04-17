# Branch Protection — Workflow UI

This document describes the required branch protection rules for the `main` branch
as they relate to the Workflow Designer UI project.

## Required status checks

The following CI jobs must pass before a PR can merge:

1. **build-dotnet** — .NET solution builds with `-warnaserror`.
2. **test-dotnet (EnumParity)** — Enum parity reflection gate.
3. **test-dotnet (DtoParity)** — DTO parity reflection gate.
4. **test-dotnet (YamlRoundTrip)** — YAML round-trip golden gate.
5. **test-client** — Client lint + format + test + build.
6. **coverage-gate** — Coverage artifacts uploaded and thresholds met.

## Review policy

- Require pull-request review by at least one CODEOWNER.
- Dismiss stale reviews on push.
- Require linear history (no merge commits).

## How to configure (GitHub UI)

1. Go to **Settings → Branches → Branch protection rules → Add rule**.
2. Set **Branch name pattern** to `main`.
3. Check **Require a pull request before merging**.
4. Check **Require approvals** (at least 1).
5. Check **Dismiss stale pull request approvals when new commits are pushed**.
6. Check **Require status checks to pass before merging** and add the 6 checks above.
7. Check **Require linear history**.
8. Save.

## TODO

- [ ] Create `@workflow-owners` team in GitHub org and add members.
