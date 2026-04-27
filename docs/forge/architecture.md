# Forge — architecture

A two-process system: an ASP.NET Core minimal API and a Vite/React SPA.
They share a contract through hand-written types (`types.ts` ↔
`Forge.Api/Contracts/`) and the OpenAPI doc at `/v1/openapi.json`.

```
                         ┌────────────────────────┐
            HTTP REST    │   Forge.UI (React)     │
        ┌────────────────│   Vite dev / nginx     │
        │  + SignalR     └────────────────────────┘
        ▼
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
│  Forge.Api       │───▶│  IForgeProvider  │───▶│  ADO REST        │
│  (ASP.NET 8)     │    │   - AdoProvider  │    │  GitHub REST     │
│                  │    │   - GitHubProvider│   └──────────────────┘
│  ┌─────────────┐ │    └──────────────────┘
│  │  SQLite DB  │ │       ▲
│  │  forge.db   │ │       │  webhooks
│  └─────────────┘ │       │  (HMAC verified)
└──────────────────┘       │
                           └─── ADO Service Hooks / GitHub webhooks
```

## Request flow (write)

1. UI calls `forgeApi.move/create/...` — typed wrapper around `fetch`.
2. `ForgeSecretMiddleware` checks `X-Forge-Secret` (Phase 7.6).
3. `IdempotencyMiddleware` looks up the `Idempotency-Key` and replays the
   cached body if seen.
4. `AuditMiddleware` buffers the request body and (post-call) writes a row
   to `forge_actions`.
5. The endpoint validates, mutates SQLite via EF Core, fans out a SignalR
   event (`card.changed`, `pr.changed`, `workstream.changed`).
6. Other tabs receive the event and update their Zustand store.

## Request flow (provider write)

The same as above, but the endpoint also calls
`IForgeProvider.<Verb>Async(req, ctx)`. `ProviderCallContext` carries:

- `UserId` — from `X-Forge-User`
- `UserPat` — from `X-Forge-{Ado,GitHub}-Pat` (if present, overrides server PAT)
- `IdempotencyKey` — from `Idempotency-Key`

If the provider call fails the endpoint returns 502 + the original error
message; the local cache is *not* mutated, so retries are clean.

## Provider read sync

`ProviderSyncService` is an `IHostedService` that ticks every
`Forge:Sync:IntervalSeconds` (jittered ±20%). For each configured provider:

1. `ListWorkItemsAsync` / `ListPullRequestsAsync`
2. `ProviderCache` upserts into SQLite, bumping `RemoteRevision`
3. SignalR broadcasts `card.changed` / `pr.changed` for clients

Webhooks short-circuit step 1: when a hook fires, the cache invalidation
runs immediately. If no hook arrives for 5 minutes the polling tick still
catches drift.

## State (UI)

Each view owns a Zustand store:

- `useBoardStore`     — cards + drag/move policy
- `usePrsStore`       — PRs + auto-merge / build actions
- `useWorkstreamsStore` — workstream grid + create / sync
- `useStateStore`     — top-level seed (epics / columns / wsStats / overview)
- `usePrLogStore`     — live build-log lines per PR id (Phase 6.7)
- `useThemeStore`     — light/dark, persisted to `forge.theme`
- `useSidebarStore`   — collapse state
- `useToastStore`     — toast queue

`useForgeHub` is a mount-once hook that opens the SignalR connection and
dispatches `card.changed`, `pr.changed`, `pr.log` into the right store.

## Telemetry

Server-side: `ILogger<T>` throughout, message names are stable strings of
the form `forge.action.<noun>.<verb>` so they're greppable in any sink.
Audit middleware persists writes to `forge_actions` (read-only at `/audit`).

Client-side: `src/telemetry.ts` is a 50-entry ring buffer. `logAction()`
pushes a structured `{ at, name, attrs }` entry; the report-problem flow
serialises the buffer + correlation id to the clipboard.

## Why no separate auth service?

The app is single-tenant by design (your team, your laptop or your
internal box). For multi-tenant deployments the `ForgeAuthOptions.Mode`
field switches the middleware to JWT bearer; wire your IdP through standard
ASP.NET Core auth and you're done. A SaaS multi-tenant story is explicitly
in `forge_v2.md`.
