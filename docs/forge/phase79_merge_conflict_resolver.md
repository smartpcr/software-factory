# Phase 79 — Agent-driven merge-conflict resolver

> Operator intent 2026-05-17: "why can't generator able to resolve merge
> conflict?" → chose path **C** (full agent resolver, auto-merge on
> success). Trade-off accepted: agent's resolution lands on base without
> a human gate; mitigations below.

## Goal

When `ApproveAndMergeAsync` fails with `405` + body containing
`"Pull Request has merge conflicts"`, the orchestrator dispatches the
Engineer agent to rebase + resolve the conflicts + force-push, then
retries the merge. Only after N attempts of resolver failure does the
workstream stall the way it does today.

## Non-goals

- **Failing required-status-checks** (also 405). Distinguish via response
  body; those still stall as today (agent can't fix CI red without going
  through full review-fix).
- **Lockfile conflicts** beyond regenerate. Per-file strategy table:
  `*.lock` / `package-lock.json` / `Cargo.lock` → regenerate, not edit.
- **Submodule conflicts**, **binary file conflicts** — stall as today.

## Design

### New plumbing

1. **`IStoryGitOps.RebaseAndDetectConflictsAsync(repo, branch, baseBranch, ct)`**
   - Returns `RebaseResult { State: Clean | Conflicted | RebaseFailed, ConflictedFiles: IReadOnlyList<ConflictedFile> }`.
   - `ConflictedFile { Path, Content }` — content includes the
     `<<<<<<< / ======= / >>>>>>>` markers.
   - State machine:
     - Fetch `origin/{baseBranch}`.
     - `git rebase origin/{baseBranch}` in the worktree.
     - Exit 0 → `Clean` (caller force-pushes + retries merge).
     - Conflicts → parse `git status --porcelain` for `UU`/`AA`/`DD`
       paths → read each → return `Conflicted`.
     - Any other rebase failure (missing upstream, no commits, etc.)
       → `RebaseFailed` with detail; orchestrator falls back to stall.

2. **`IStoryGitOps.ContinueRebaseAndPushAsync(repo, branch, resolvedFiles, ct)`**
   - Writes each resolved file's content to disk.
   - `git add` each path.
   - `git rebase --continue` (may loop into another conflict round if
     the rebase has multiple commits — returns `Conflicted` again, in
     which case the orchestrator loops).
   - On clean: `git push --force-with-lease=<branch>:<expected-old-sha>`
     so a concurrent push fails safely instead of clobbering.

3. **`CrewStoryArtifactStrategy.ProduceConflictResolutionAsync(spawn, ws, conflictedFiles, ct)`**
   - Engineer agent invocation tagged `conflict-resolve:{wsId}`.
   - Prompt assembles: ws.Name + ws.Summary + the workstream's recent
     deliverable Notes (so agent knows its own intent) + the
     upstream change context (last 5 commits on base since the ws
     branch diverged) + the full conflicted-file contents.
   - Agent writes resolved files into the worktree; strategy reads
     them back as `IReadOnlyDictionary<string, string>` and returns.
   - Per-file strategy table applied here: lockfile paths are NOT
     sent to the agent; strategy regenerates them by running the
     project's restore command (already known via `RepoRow.RestoreCommand`).

4. **`StorySpawnOrchestrator.TryResolveConflictsAndRetryMergeAsync(spawn, ws, prId, mergeEx, ct)`**
   - Replaces the direct call to `StallWorkstreamForMergeFailureAsync`
     in two places: Step 1's `comments.Count == 0` block (line ~3061)
     and Step 3's `approve+merge` block (line ~3101).
   - Distinguishes merge-conflict 405 (handle) from other 405s (stall
     immediately — required-status-check, branch-protection, etc).
   - Loop up to `Phase79Constants.MaxResolveAttempts = 3`:
     a. `RebaseAndDetectConflictsAsync`.
     b. If `RebaseFailed` → stall with the rebase failure detail.
     c. If `Clean` → `git push --force-with-lease` + retry merge.
     d. If `Conflicted` → `ProduceConflictResolutionAsync` → 
        `ContinueRebaseAndPushAsync` → retry merge.
   - On 3rd failure → `StallWorkstreamForMergeFailureAsync` with the
     LAST failure detail.

### Force-push audit trail

Before each force-push, post a single PR comment via
`provider.PostPrCommentAsync(prId, filePath: null, line: 0, body, agentId, ct)`:

> 🔧 Rebased onto `{baseBranch}` to resolve merge conflicts with
> {N} other recently-merged PRs. Resolved {K} file(s) — see commit
> {short-sha} for the resolution diff.

Operators can audit the commit and revert the merge if the resolution
is wrong.

### Detecting "merge conflicts" vs "blocked by required check"

`ApproveAndMergeAsync` already catches `HttpRequestException`; the
body text discriminates. Helper:

```csharp
internal static bool IsMergeConflictError(Exception ex)
{
    var msg = ex.Message;
    return msg.Contains("merge conflicts", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("Merge conflict", StringComparison.OrdinalIgnoreCase);
}
```

Required-status-check failures usually surface as `"Required status checks not yet passed"` or `"Branch protection rule"` in the body — neither matches.

## Stages

- [ ] **Stage 1 — `IStoryGitOps` additions**
  - [ ] `RebaseAndDetectConflictsAsync` + types (`RebaseResult`,
        `ConflictedFile`).
  - [ ] `ContinueRebaseAndPushAsync`.
  - [ ] `RealStoryGitOps` impl (uses `git` CLI in the worktree).
  - [ ] `LocalStoryGitOps` test stub.
- [ ] **Stage 2 — Strategy resolver**
  - [ ] `ProduceConflictResolutionAsync` in `CrewStoryArtifactStrategy`.
  - [ ] Per-file strategy table (lockfile regenerate).
  - [ ] Engineer agent prompt template.
- [ ] **Stage 3 — Orchestrator wiring**
  - [ ] `TryResolveConflictsAndRetryMergeAsync`.
  - [ ] `IsMergeConflictError` discriminator.
  - [ ] Replace direct `StallWorkstreamForMergeFailureAsync` calls in
        DriveReviewAsync's two merge-fail catches.
  - [ ] Force-push audit comment.
- [ ] **Stage 4 — Tests**
  - [ ] `Phase79MergeConflictResolverTests`: 3 scenarios.
    - Resolver succeeds in 1 attempt → ws advances to `merging`.
    - Resolver succeeds on 2nd attempt (multi-commit rebase) → 
      ws advances.
    - Resolver fails 3× → ws stalls with the same StallReason as
      today.
  - [ ] `IsMergeConflictErrorTests`: positive + negative cases.
- [ ] **Stage 5 — Run + cutover**
  - [ ] Stop API, build, restart.
  - [ ] Manually trigger resolution on PRs #35 / #36 / #38 via spawn
        retry.
  - [ ] Verify each ws advances or stalls with clear feedback.
- [ ] **Stage 6 — Docs**
  - [ ] Update `forge_implementation.md` § Phase 79.
  - [ ] Note the trade-off (no human gate) and the audit-comment
        mitigation.

## Risk register

| Risk | Mitigation |
|---|---|
| Agent picks wrong intent on semantic conflict; bad code lands on base | Audit PR comment + commit message tagged `conflict-resolve` so operator can revert. Future: surface "agent resolutions today" digest. |
| Force-push erases reviewer comments | Force-push uses `--force-with-lease`. Pre-push comment posted before force so audit trail survives. Reviewer comments on the rebased-away commits get orphaned but remain in the PR thread. |
| Infinite loop if rebase keeps producing new conflicts | Hard cap at 3 attempts (`Phase79Constants.MaxResolveAttempts`). After cap: stall + carry the last error detail. |
| Resolver races with operator manual rebase | Per-spawn lock added in Phase 78 already prevents concurrent ticks. Operator-initiated git push outside Forge.Api → `--force-with-lease` fails safely. |
| Lockfile conflicts that the restore command can't resolve | Per-file strategy table falls back to stall if regenerate exits non-zero. |
| 405 on failing required-status-check looks like merge conflict | Discriminate via response body keyword; conservative — only "merge conflicts" exact wording matches. |

## File map

| File | Change |
|---|---|
| `src/forge/Forge.Api/Services/Stories/IStoryGitOps.cs` | Add new methods + types. |
| `src/forge/Forge.Api/Services/Stories/RealStoryGitOps.cs` | Implement new methods (git CLI). |
| `src/forge/Forge.Api/Services/Stories/LocalStoryGitOps.cs` | Stub for tests. |
| `src/forge/Forge.Api/Services/Stories/IStoryArtifactStrategy.cs` | Add `ProduceConflictResolutionAsync`. |
| `src/forge/Forge.Api/Services/Stories/CrewStoryArtifactStrategy.cs` | Implement resolver invocation + lockfile regenerate. |
| `src/forge/Forge.Api/Services/Stories/StorySpawnOrchestrator.cs` | `TryResolveConflictsAndRetryMergeAsync`, discriminator, wire-in. |
| `src/forge/Forge.Api/ScoringConstants.cs` | `MaxResolveAttempts = 3`. |
| `src/forge/Forge.Api.Tests/Phase79MergeConflictResolverTests.cs` | New tests. |
| `docs/forge_implementation.md` | Phase 79 entry. |
