# Forge — e2e-refactor rollout runbook (operator-facing)

> Companion to [e2e.md](e2e.md) — the implementation plan for splitting QA
> out of its own Phase 5 and into per-stage `qa-e2e` workstreams. This
> runbook is what an operator reaches for **during the rollout window**
> when a legacy spawn is stuck, when production tips over and needs a
> back-out, or when a `lab-*` story needs credentials seeded.

---

## 1. Recognising a legacy spawn vs a new spawn

A **new-shape** spawn (created under the post-refactor orchestrator) has
all of these signals:

- `spawn.QaPrId` is `NULL` — never populated. The per-stage qa-e2e
  workstreams open their own `[e2e]`-prefixed PRs; there is no
  story-level QA artefact.
- `workstreams` table carries at least one row with
  `Category = 'qa-e2e'` (where the parent stage in
  `implementation-plan.md` declared `### Test Scenarios`).
- `spawn.Phase` rotates **plan → decompose → implement → review →
  done**. It never lands at `'qa'`.

A **legacy** spawn (paused mid-flight under the pre-refactor
orchestrator) has all of these signals:

- `spawn.QaPrId` is set to a real PR id (the legacy batch QA PR).
- `workstreams` table has zero rows with `Category = 'qa-e2e'` — every
  row is `Category = 'impl'`.
- `spawn.Phase` is `'qa'` (or, if the spawn made it through QA, it sits
  at `'done'` with `QaPrId` still populated as historical context).

### Quick SQL probe

```sql
-- Run against the spawn's per-product DB (path under Forge:Db:Folder).
SELECT  s.Id            AS spawn_id,
        s.StoryId,
        s.Phase,
        s.QaPrId,
        CASE
          WHEN s.QaPrId IS NULL
           AND EXISTS (SELECT 1 FROM workstreams w
                       WHERE w.StorySpawnId = s.Id AND w.Category = 'qa-e2e')
          THEN 'new-shape'
          WHEN s.QaPrId IS NOT NULL OR s.Phase = 'qa'
          THEN 'legacy'
          ELSE 'undetermined (no qa-e2e rows AND no QaPrId — may be a pre-decompose spawn)'
        END             AS shape
FROM    story_spawns s
WHERE   s.StoryId = '{storyId}';
```

The `'undetermined'` row is the edge case to watch for: a spawn paused
before decomposition finished. It can drain through either path
depending on whether you allow the orchestrator to re-decompose
(produces new-shape qa-e2e rows) or force it through legacy drain
(re-set `Phase = 'qa'` and supply `QaPrId`).

---

## 2. Manually advancing a stuck legacy spawn to `done`

A legacy spawn parked at `Phase = 'qa'` continues to drain through
`StorySpawnOrchestrator.DriveQaAsync` (the back-compat code path kept
alive by [Stage 5.3](e2e.md#stage-53-legacy-phaseqa-drain-path)). The
common failure modes + their recoveries:

### 2a. Drain stalled because the legacy QA PR never opened

Symptom: `spawn.QaPrId IS NULL`, `spawn.QaBranch IS NULL`, the spawn is
at `Phase = 'qa'`, and the orchestrator's drive log says
`DriveQaAsync no-op for spawn ...`.

Fix: tick the orchestrator once. The legacy path re-detects the missing
QA PR and opens it on the next pass. If that doesn't fire (the spawn
ALSO carries qa-e2e workstream rows — the [Stage 5.3 new-shape guard](e2e.md#stage-53-legacy-phaseqa-drain-path)
short-circuits in that case), see the back-out section below.

```bash
# Trigger one drive from the operator API. Replace {storyId}.
curl -X POST http://localhost:5180/api/stories/{storyId}/spawn/retry \
     -H "X-Forge-User: po@forge.local"
```

### 2b. Legacy QA PR opened but step-def PRs never followed

Symptom: `spawn.QaPrId` is set, the PR row's `Col = 'merged'`, but no
`[qa-steps] ...` PRs exist for the spawn's workstreams.

Fix: confirm every workstream's `Status = 'complete'` (the legacy
step-def emission gates on that). If a workstream is stuck at `'review'`
or `'merging'`, walk it forward with the workstream-detail UI's resume
button — the legacy code path resumes from wherever you left it without
re-opening the merged QA PR.

### 2c. All step-def PRs merged but `Phase` won't flip to `done`

The legacy completion gate requires every workstream's full
`PrsJson` list to be merged (impl PR **and** step-def PR). If one of the
PR ids in `PrsJson` references a PR that was abandoned or never opened,
the gate hangs.

Fix: read the workstream's `PrsJson`, identify the dangling PR id, and
either merge it manually (if it really should be merged) OR remove the
id from the JSON list (if it was a phantom from a retry race):

```sql
-- AFTER backing up the row. Replace {wsId}.
UPDATE workstreams
SET    PrsJson = json_remove(PrsJson, '$[1]')  -- index of the dangling id
WHERE  Id = '{wsId}';
```

Then trigger another drive — the gate should release.

---

## 3. Backing out the migration

If production tips over (e.g. the orchestrator throws on the new
`Category` column, the per-stage qa-e2e routing opens the wrong PRs),
the migration backs out cleanly because every server-side change was
**additive**:

### 3a. SQL back-out

The `Phase79Migration` added the `workstreams.Category` column with
default `'impl'`. Reset every row to that default so a prior orchestrator
binary doesn't see unexpected `'qa-e2e'` values:

```sql
-- Step 1: snapshot, then reset.
.backup forge-pre-backout.db

UPDATE workstreams SET Category = 'impl';
```

Workstream rows with `Category = 'qa-e2e'` previously created qa-e2e
PRs in the `WorkstreamRow.PrsJson` list. After this UPDATE, the prior
orchestrator binary treats them as impl rows + may try to re-open them
as impl PRs. Two recovery paths:

- **Soft back-out** (rows + PRs survive): mark each former qa-e2e row's
  `Status = 'cancelled'` so the prior orchestrator's claim sweep skips
  it. The PR rows in the `prs` table stay untouched; the operator can
  abandon them upstream by hand.

  ```sql
  UPDATE workstreams
  SET    Status = 'cancelled'
  WHERE  Id IN (SELECT Id FROM workstreams_snapshot_before_backout
                WHERE Category = 'qa-e2e');
  ```

  Keep a `workstreams_snapshot_before_backout` table around (created via
  `CREATE TABLE ... AS SELECT * FROM workstreams`) so you can identify
  the former qa-e2e rows after the UPDATE above wipes the discriminator.

- **Hard back-out** (rows deleted): drop the rows + their PRs entirely.
  Riskier; only do this if the upstream PRs are also being abandoned.

  ```sql
  DELETE FROM prs WHERE Workstream IN (SELECT Id FROM workstreams WHERE ... was qa-e2e);
  DELETE FROM workstreams WHERE ... was qa-e2e;
  ```

### 3b. Binary back-out

The pre-refactor orchestrator binary ignores the `Category` column
(EF Core's snake-strict reader silently drops columns it doesn't know
about). Redeploy the prior binary AFTER the SQL back-out above, then
restart Forge.Api:

```powershell
# Stop the running server cleanly.
scripts\run-forge-ui.ps1 -Stop

# Roll back the bits in-place (assumes you keep tagged binaries around).
git checkout {prior-tag} -- src/forge/Forge.Api
dotnet build src/forge/Forge.Api/Forge.Api.csproj

# Restart against the back-out DB.
scripts\run-forge-ui.ps1 -ServerOnly
```

Verify by tailing the boot log — no `Phase79Migration` re-apply attempts
should fire (the prior binary's `PhaseMigrationCatalog` doesn't
register it), and the `/v1/openapi.json` probe should return 200.

### 3c. Rolling forward again

Reverting the back-out is symmetric: redeploy the post-refactor binary
+ tick the orchestrator. The new orchestrator's `Phase79Migration`
re-applies idempotently (it probes `pragma_table_info` before
`ALTER TABLE`); workstream rows you cancelled in the soft-back-out
above stay cancelled until you re-open them via the workstream-detail
UI's resume button.

---

## 4. Seeding lab credentials (`lab-wsfc` / `lab-bare-metal` stories)

A `lab-*` story's qa-e2e workstream renders an ADO pipeline YAML +
GitHub workflow that pull three env-vars from the lab credential
store:

- `LAB_CLUSTER_FQDN` — fully-qualified cluster head node
- `LAB_ADMIN_USER` — cluster admin account
- `LAB_ADMIN_PASSWORD` — cluster admin password

Both providers must be populated (per the §8 lab-secrets-incomplete
linter rule). The pipeline YAML the renderer emits names the providers
by hard convention — `forge-lab-kv` for ADO Key Vault + `lab-wsfc` /
`lab-hw` for the GitHub environment — so the seed steps below are
copy-paste safe.

### 4a. Azure Key Vault (ADO side)

```bash
# Prereq: az login + role assignment "Key Vault Secrets Officer" on forge-lab-kv.

az keyvault secret set --vault-name forge-lab-kv \
                       --name LAB_CLUSTER_FQDN \
                       --value 'lab-wsfc-cluster-01.contoso.internal'

az keyvault secret set --vault-name forge-lab-kv \
                       --name LAB_ADMIN_USER \
                       --value 'forge-lab\admin'

az keyvault secret set --vault-name forge-lab-kv \
                       --name LAB_ADMIN_PASSWORD \
                       --value '<long-random-secret>'
```

The pipeline references the secrets via the `AzureKeyVault@2` task
named in the renderer's output:

```yaml
# tests/e2e/{phaseSlug}/azure-pipeline.yml — emitted by E2eLabRenderer
steps:
  - task: AzureKeyVault@2
    inputs:
      ConnectedServiceName: forge-lab-sc
      KeyVaultName: forge-lab-kv
      SecretsFilter: 'LAB_CLUSTER_FQDN,LAB_ADMIN_USER,LAB_ADMIN_PASSWORD'
```

The service connection `forge-lab-sc` must exist in the ADO project
(create it once via `Project Settings → Service Connections → New
service connection → Azure Resource Manager` with managed identity).

### 4b. GitHub environment (GH side)

```bash
# Prereq: gh auth login + repo admin permission.

# Seed against the right environment name — `lab-wsfc` for WSFC stories,
# `lab-hw` for bare-metal stories. The renderer picks the env name
# from the parsed setup type.
GH_ENV=lab-wsfc

gh secret set LAB_CLUSTER_FQDN     --env "$GH_ENV" --body 'lab-wsfc-cluster-01.contoso.internal'
gh secret set LAB_ADMIN_USER       --env "$GH_ENV" --body 'forge-lab\admin'
gh secret set LAB_ADMIN_PASSWORD   --env "$GH_ENV" --body '<long-random-secret>'
```

The workflow references them via `${{ secrets.* }}` — matching the
renderer's output:

```yaml
# .github/workflows/e2e-{phaseSlug}.yml — emitted by E2eLabRenderer
jobs:
  e2e-{phaseSlug}:
    runs-on: [self-hosted, wsfc-lab]
    environment: lab-wsfc        # <- gates secret release on environment review
    steps:
      - name: Run e2e for ...
        run: dotnet test --filter Category=e2e
        env:
          LAB_CLUSTER_FQDN: ${{ secrets.LAB_CLUSTER_FQDN }}
          LAB_ADMIN_USER:   ${{ secrets.LAB_ADMIN_USER }}
          LAB_ADMIN_PASSWORD: ${{ secrets.LAB_ADMIN_PASSWORD }}
```

### 4c. Local-developer convention

A developer running the e2e suite on their workstation populates the
same three env-vars in their shell — the step-def base class
(`tests/Steps/{storyId}/E2eBase.cs`, emitted once per spawn by
`E2eBaseRenderer`) calls `RequireEnv("LAB_CLUSTER_FQDN")` and throws
`SetupSkippedException` if it's unset. Locally the framework yellow-
skips on that exception; CI hard-fails (controlled by
`appsettings.e2e.json`'s skip policy).

```powershell
# Local — populate, then run the e2e suite for one phase.
$env:LAB_CLUSTER_FQDN     = 'lab-wsfc-cluster-01.contoso.internal'
$env:LAB_ADMIN_USER       = 'forge-lab\admin'
$env:LAB_ADMIN_PASSWORD   = '<long-random-secret>'

dotnet test --filter Category=e2e
```

If a `SetupSkippedException` fires on CI, the test reporter shows the
missing var name — operator's first move is to confirm the secret
exists in the right Key Vault / GH env, with the same casing as the
renderer emitted.

---

## 5. Open follow-ups

- **Cross-story e2e** (e.g. a phase whose tests depend on another
  story's deployment) is out of scope for the Q2 2026 refactor. See
  `e2e.md` §12 "Out-of-scope" for the deferral.
- **Lab credential rotation** — the operator's responsibility, not
  Forge's. Set the Key Vault secret's expiration when seeding so the
  rotation reminder fires before the pipeline starts failing on a
  stale credential.

---

## 6. Quick-reference card

| Situation | Action | Reference |
|-----------|--------|-----------|
| Legacy spawn drain stalled at `Phase=qa` | `POST /spawn/retry` to tick the orchestrator | §2a |
| Legacy spawn at `Phase=qa` but ALSO carries qa-e2e rows | Stage 5.3 guard short-circuits; either remove qa-e2e rows (back-out) or migrate spawn forward | §3 |
| New-shape spawn, `qa-e2e` workstream stuck at `planning` | Confirm impl sibling's `Status='complete'` + PR merged; the dep gate keys on `Status` not on `Col='merged'` | [e2e.md §5](e2e.md#stage-51-driveimplementasync-branches-on-category) |
| Lab pipeline `SetupSkippedException` on CI | Seed secret in BOTH Key Vault AND GH environment | §4a + §4b |
| Need to back out the entire refactor | SQL reset `Category='impl'` + redeploy prior binary | §3a + §3b |

---

*This runbook covers the Q2 2026 e2e refactor rollout. As the migration
matures + legacy spawns drain out, sections 2 + 3 lose their audience
— a future cleanup pass can fold them into a "deprecated paths"
archive once the operator confirms no `Phase='qa'` spawns remain in
production.*
