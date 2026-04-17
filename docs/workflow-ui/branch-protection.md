# Branch Protection — Workflow UI

This document describes the required branch protection rules for the `main` branch
as they relate to the Workflow Designer UI project.

## Required status checks

The following CI jobs must pass before a PR can merge (names match the `name:` field
in `.github/workflows/ci-workflow-ui.yml`):

1. **Build (.NET)** — .NET solution builds with `-warnaserror`.
2. **Test (.NET)** — Contract tests with code coverage collection.
3. **Parity (EnumParity)** — Enum parity gate verifying C#↔TS enum contract.
4. **Parity (DtoParity)** — DTO parity gate verifying C#↔TS shape contract.
5. **Parity (YamlRoundTrip)** — YAML round-trip golden file contract.
6. **Test (Client)** — Client lint + format + test + build.
7. **Coverage gate** — Downloads coverage artifacts and enforces 100% threshold on new code (hard failure when missing).

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
6. Check **Require status checks to pass before merging** and add the 7 checks above.
7. Check **Require linear history**.
8. Save.

## TODO

- [ ] Create `@workflow-owners` team in GitHub org and add members.
