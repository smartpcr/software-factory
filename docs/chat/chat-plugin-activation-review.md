# Live chat activation review — September 11, 2026

The reviewed activation completed after the user's explicit **yes** in chat.
The host now runs the plugin chat engine, and the user's personal Telegram account
is verified for the new connection. Forge.Api stayed running throughout (PID 70188).
Channel selection for the new connection and legacy repair remain separate open work.

## Verified state before activation (21:38 UTC)

- [x] Dedicated Telegram manager registered and available.
- [x] User completed real Telegram managed-bot consent and saved one enabled
      connection, `@f176773deff2b7b7dfb7e011fb5a5bot` (installation `8908278778`).
- [x] Connection grants match the user's saved selection: code-intelligence,
      failover-cluster, local, raft and release.
- [x] Authenticated live status: no personal identity links, no destinations,
      `hostReason: legacy-engine-active`, `interactiveAvailable: false`.
- [x] Read-only migration preview: revision 0, phase `not-imported`, legacy
      credential available, one legacy destination, no confirmed identity links.
- [x] Existing history totals 40 notifications: 8 failed, 5 acted, 12 sent,
      4 answered and 11 superseded. No pending/sending notification was reported.
- [x] The one verified parent candidate is `local / spawn-local-NET10-MIGRATION`
      (`download-net10-migration`). The current operator can manage it.

Two old scope entries are unverified: a NET10 entry with no product ownership,
and `release / spawn-release-RELEASE-PROVIDER`. The preview also reports 14
unresolved-scope, 24 ownership-unverified and one destination-missing issue across
topics/history. These counts can overlap on a record. Do not infer ownership or
replay those old notifications to finish new-bot setup.

The read-only check used a temporary local operator session and logged it out
afterward. Its before-state is retained in
`.tmp-build/chat-manager/chat-activation-result.json`.

## Approved activation procedure

Use the existing [migration API](chat-plugin-migration.md), with fresh revision
and source-hash checks immediately before each write:

1. Import the legacy connection, destination and history using its existing
   credential reference. Import leaves the legacy runtime active.
2. Cut over chat ownership to `plugins`, retaining Host alerts and selecting only
   `local / spawn-local-NET10-MIGRATION` with metadata content. Leave the two
   unverified old scope entries disabled for separate repair. Existing legacy
   remote actions are off; do not turn them on as part of migration.
3. Verify that the newly authorized Telegram connection starts receiving and that
   host readiness becomes interactive. Preserve all current product grants.
4. Complete a personal identity challenge through the user's Telegram account,
   using an interactive popup for account confirmation. Never grant identity from
   the bot token, an unconfirmed subscriber, or a manually inserted database link.
5. Select and validate the destination, then enable only the user-selected story
   or Host alerts route. Provider test messages require explicit user authorization.

The migration drains and switches chat processing in the running service. It does
not restart Forge.Api or agents. It changes the chat engine for the whole host,
including existing Telegram routes, so host-wide activation requires user approval.
The eight failed historical notifications are retained; retrying them is separate.

- [x] User approves the reviewed host-wide chat activation. The user reported that
      the attempted input popups were not visible; their results were not used as
      approval. The explicit **yes** to the direct chat question authorized activation.
- [x] Import and cut over at 22:06 UTC: import revision 1 (`legacy` / `imported`),
      cutover revision 2 (`plugins` / `plugins`). New connection receiver reaches
      `ready`; management reports `hostReason: null`, `interactiveAvailable: true`.
      All 40 historical notifications retain their status. Existing Host alerts
      are retained; the verified local parent has one noninteractive metadata
      subscription. The new connection's saved configuration and grants are unchanged.
- [x] Complete personal identity proof at 22:10 UTC. The authenticated Telegram
      Desktop account redeemed a sender-bound challenge through the new bot.
      Forge's live API and Settings report **Identity verified for this installation**.
      No identity was inferred from a credential or inserted directly into storage.
- [ ] Select/validate a channel and configure the intended route.
- [ ] Repair unverified legacy scope/history entries separately.

Post-import preview still reports repair issues: 37 unresolved-scope, 24
ownership-unverified, one legacy-control-revision-unverified and one destination-missing.
Counts can overlap; they remain visible and were not dismissed or replayed. Both
unverified scope entries remain disabled. This activation does not close the full
real-provider validation matrix, Slack setup or a production rollback exercise.

Evidence: `.tmp-build/chat-manager/chat-activation-result.json`,
`telegram-identity-linked.json` and `telegram-identity-linked.png`.
Temporary operator sessions were logged out after verification.