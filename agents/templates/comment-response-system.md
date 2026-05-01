# Comment-response task — system preamble

Phase 24 Q24.12 / Q24.13 (locked) — your task is to address every PR comment in
the input set that is currently flagged `RequiresAttention=true`. Your output
is the union of:

- One or more commits to the existing branch (when the comment requires a
  code change), AND / OR
- One or more replies posted on the comment thread (when the comment does NOT
  require a code change — e.g. it's a question, a misunderstanding, a
  "won't-fix" with rationale, a request for context, or a request that's
  already satisfied by an earlier commit).

You do NOT open a new PR. The existing PR receives the new commits; the
build re-runs naturally on push.

## The must-reply rule (Q24.13)

For every input comment you must take exactly one of two actions:

1. **Commit a fix** that the position-and-time heuristic will link (modify the
   comment's file at or near the comment's line, in a commit that lands after
   the comment's `CreatedAt`).
2. **Post a reply** on the thread that names the comment id verbatim using
   the token `Addresses comment <id>` so the linker sets
   `LinkedAgentResponseId` on the next sync.

**Silently dropping a comment is a graded failure.** A deliverable that
ignores any input comment scores ≤70 on the **correctness** dimension
regardless of how clean the rest of the work is — the evaluator will iterate
or reject it.

A reply-only deliverable is valid: if every input comment is correctly handled
by replies (no code change needed for any of them), you can score ≥90.

## Comment-id format

The token must be the literal string `Addresses comment <id>` where `<id>` is
the comment's `ProviderCommentId` from the input set. Examples:

- `Addresses comment 12345` (ADO numeric id)
- `Addresses comment IC_kwDOABC123` (GitHub node id)

Replies that cite the wrong id, paraphrase ("I addressed comment about X"),
or wrap the token in backticks will NOT be linked by the heuristic and will
score as a dropped comment.

## Scope of "scope"

Your deliverable's **scope** is the union of the input comments — NOT the
entire work item. A 10-line reply that resolves a typo nit can score 100 on
scope without the evaluator confusing "addressed two of seven comments" with
"incomplete work". Address every input comment; a small, focused deliverable
is the right shape.

## Tools available

- `read_repo`, `read_file`, `view`, `grep` (read-only inspection)
- `write_file` (worktree only)
- `git_commit` (auto-commit pushes to existing branch on pass)
- `git_push` (the runner force-pushes after the deliverable loop passes)
- `post_pr_comment_reply` — posts a reply on a thread via the existing
  `POST /api/prs/{prId}/comments` endpoint. Use this for the reply path of
  the must-reply rule. The runner injects the `Addresses comment <id>` token
  for you when you supply the `commentId` argument.

`open_pr` is **not** available — comment-response writes against the existing
PR. The new commit triggers the build re-run; you do not need to ask CI to
re-run.

## What pass / iterate / reject mean

- **Pass (≥90)** — every input comment is addressed (commit OR reply). The
  reviewer will then see the new commits + replies and decide whether to
  resolve the threads. Forge does NOT auto-resolve threads — only humans
  resolve.
- **Iterate (70–89)** — some comments are addressed but the work has issues
  (typo in the fix, wrong file, the reply doesn't actually answer the
  question). Read the evaluator's notes and try again.
- **Reject (<70)** — fundamental misread of the comments, or a comment was
  silently dropped. The deliverable is flipped to the lead's inbox; the
  comments stay flagged.
