# Forge — v2 backlog

> Items intentionally cut from v0.1.0. Each row carries the reason so we
> don't relitigate the decision when picking up next iteration. The bar
> for adding rows here is "would have been nice but did not block a real
> user flow in v0.1".

## Refactor backlog (deferred from Phases 0–7)

These overlap with Phase R in `forge_implementation.md`. Reproduced here
so v2 planning has one place to look.

| ID | Item | Why deferred |
|----|------|--------------|
| R.1 | Decompose monolithic CSS into CSS Modules / vanilla-extract | Pixel-fidelity to the prototype was the v0.1 contract; touching CSS risks drift |
| R.2 | Replace inline SVGs with an icon component / sprite | Same — fidelity-first |
| R.4 | Style Dictionary design tokens | Not load-bearing while we have one consumer |
| R.5 | A11y pass (focus rings, ARIA roles, contrast audit) | Needs a dedicated audit + screen-reader testing |
| R.6 | Virtualise long lists (Backlog, audit) once > 200 rows | No customer hit that limit yet |
| R.7 | Storybook for components | Worth doing once the design system is stable (post-R.1) |

## Provider gaps

| Item | Why deferred |
|------|--------------|
| ETag / `If-None-Match` on background sync (5.7) | Polling + jitter is enough at our scale; ETag adds complexity |
| Conflict-resolution UI on `card.changed` 409 (5.9) | Server returns the conflict envelope; UI shows a generic toast — no observed loss yet |
| Encrypted-at-rest PAT storage in SQLite | Currently relies on user-secrets / env / OS keychain; full DPAPI/Key Vault wrapper is its own change |
| Real reviewer-id resolution in ADO `SetReviewerVoteAsync` | Today we PATCH `/reviewers/me` which only works for the PAT owner |
| ADO `DefaultPipelineId` discovery | We require the operator to set it; auto-discovery would call `/_apis/pipelines` once on boot |
| GitHub `workflow_dispatch` filename auto-discovery | Same — we POST to `/dispatches` generically; per-workflow dispatch needs a config map |

## UX

| Item | Why deferred |
|------|--------------|
| Multi-select on the board | Drag/keyboard story for "move 3 cards at once" needs design |
| Inline edit on the card (title, points) without opening the modal | One round-trip works; saving "live edits" needs optimistic / conflict story |
| Saved filter sets ("my watch") | Watch chip exists with a hard-coded id list; pluggable saved filters need persistence |
| Diff viewer in PR detail | Today we show description + log tail; a real diff means embedding monaco or a server-side render |
| Line-level PR comments UI | Server `CommentOnPullRequestAsync` supports line comments where the provider does; the UI panel is single-textarea only |

## Auth & multi-tenant

| Item | Why deferred |
|------|--------------|
| Wire the Azure AD JWT bearer pipeline behind `Forge:Auth:Mode=azure-ad` | Skeleton config is in `ForgeAuthOptions`; full middleware + RBAC is a separate change |
| Per-tenant data isolation (rows scoped by org) | v0.1 is single-tenant. Multi-tenant would refactor every EF query + audit row |
| OAuth handoff for ADO/GitHub instead of PAT-in-secret | Removes the "where do I store PATs" problem entirely |

## Observability

| Item | Why deferred |
|------|--------------|
| OpenTelemetry SDK wiring (`forge.view.*`, `forge.action.*` as spans, not log lines) | Today these are `ILogger` messages with stable names — greppable but not exportable to Tempo/Jaeger without OTel auto-instrumentation |
| OTel metrics for sync errors / queue depth / merge latency | Same — counters today are log-line counts |
| In-app log viewer (tail of `forge.action.*`) | `/audit` covers writes; non-write logs are stdout-only |

## Packaging & ops

| Item | Why deferred |
|------|--------------|
| Helm chart / k8s manifest | docker-compose covers single-host; a chart needs an opinionated ingress + secret story |
| Auto-migrations + safe schema upgrades | EF `EnsureCreated` is fine for v0.1 (no breaking schema change yet); migrations needed before the first incompatible bump |
| Backup / restore command for `forge.db` | Trivial today (copy the file), but a built-in `forge backup` wrapper would be friendlier |
| Pre-built published images on a public registry | Operators build their own from the Dockerfile today |

## Things we explicitly are NOT doing

These came up during planning and were ruled out, not deferred. Listed so
future "should we…?" threads can find the prior decision.

- **Mobile UI.** The board / timeline density doesn't translate; a mobile
  build would be a different product.
- **In-app PAT entry on `/settings`.** Storing PATs through the SQLite
  cache without a full encryption story was rejected as a security risk.
  Use user-secrets / env / OS keychain.
- **Real-time agent runner integration.** Agents (`A1`, `E4`, …) are
  presentation-layer labels in v0.1. A future `IAgentRunner` would let the
  card actually delegate to a process — explicitly out of scope for v0.1.
