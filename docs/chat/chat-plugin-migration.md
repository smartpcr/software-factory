# Legacy Telegram migration and rollback

Stage 6 provides an explicit, repeatable migration of an existing legacy bridge.
The implementation has been rehearsed against isolated databases and HTTP fixtures;
see [Stage 6 validation](chat-plugin-stage6-validation.md). A real host cutover is
an operator action. Load this backend at an approved Forge restart before using
these commands. The migration commands themselves do not restart Forge or agents.

## Review and import

Use a paired host administrator. Selecting parents also requires product-owner
membership for each selected product; host administration does not bypass that
permission. Keep the Stage 1S slice disabled. Finish its separate live gates as
required by the [implementation plan](chat-plugin-implementation-plan.md).

The source is the effective startup configuration loaded from the Stage 0A local
file, environment and command line. No launcher export or tracked JSON patch is
needed. The original DPAPI blob stays in place; the imported connection refers to
it through the read-only `legacy-host` store. An environment/configuration token
is also referenced without copying it into the journal. Keep that original source
available until replacing the credential through Chat settings. Import checks the
token's bot-ID prefix locally; it does not contact Telegram.

```powershell
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$preview = ./scripts/migrate-chat-plugin.ps1 -WebSession $session `
  -PairingCode (Read-Host 'One-use Forge browser pairing code' -AsSecureString)
$preview | ConvertTo-Json -Depth 10

# Review the source, canonical product/spawn IDs, links, issues and counts first.
$import = ./scripts/migrate-chat-plugin.ps1 -Action Import -WebSession $session `
  -ExpectedRevision $preview.revision -SourceHash $preview.sourceHash -BotId '12345'
```

Preview writes no chat configuration and performs no provider I/O. It includes
legacy options, configured destinations, topic/scoped ownership, confirmed identity
targets, unconfirmed subscriber count, cursors and notification counts. Custom
legacy Bot API endpoints and non-Telegram settings are reported as unsupported;
import refuses to silently switch their provider endpoint.

Import creates the disabled `legacy-telegram` connection, destinations, Host alerts,
disabled parent settings, bindings and one historical delivery per notification.
It is safe to repeat using the newly returned revision. An interrupted transaction
leaves its earlier committed revision intact. A stale revision or source hash is
rejected. No secret value appears in the preview or import journal.

Closed, ambiguous or unverifiable topics and notifications carry repair issues.
Pending unverified deliveries are held with `blocked` state. Their original state,
known address and message history remain available; import never guesses a child
owner or sends an unresolved alert into another parent's topic.

## Cut over

```powershell
$active = ./scripts/migrate-chat-plugin.ps1 -Action Cutover -WebSession $session `
  -ExpectedRevision $import.revision -Activate @(
    @{ productId = 'your-product-id'; spawnId = 'your-parent-id'; contentMode = 'metadata' }
  )
```

An empty `Activate` array enables only the permanent Host alerts. Explicit parent
choices cover all verified current and future children. Other parents remain off;
enabling a completed parent does not restart work. Use `contentMode='redacted'`
only when the host already permits redacted questions for that product. Metadata
mode keeps question responses in Forge. Legacy remote-action opt-in determines the
initial interactive setting; it never creates operator authority.

The command stops admission of new legacy operations, waits for sends, polls and
dispatch to finish, refreshes the import and final cursor, and commits the engine
owner in one control-DB transaction. Producers share the commit gate: an accepted
event is persisted on one side of that handoff. Open legacy dedupe keys survive
the change, so re-emitting the same alert does not itself cause a replay.

The plugin receiver starts after that commit. Startup loads this persisted owner
before either engine runs. `Forge:Chat:Enabled=false` remains a master stop; an
explicit migration opts in when that setting is absent. Do not try to roll back
by changing `Forge:Chat:Engine` or replacing database files.

Old Telegram workstream callbacks, question choices and reply anchors use the new
shared permission and revision checks. A choice must match the original button
label and current question revision. Reused/ambiguous question revisions require
repair or an answer in Forge. Multi-question free text is accepted only when one
question remains unambiguous. Old host acknowledgement buttons lead to Host
settings, consistent with notification-only Host alerts. Revoking the original
confirmed link immediately revokes its migration alias.

While compatibility is active, `/api/chat/test-send` and `/api/chat/link-token`
target the imported connection. Test-send defaults to the configured supergroup
before the first allowed chat; an explicit chat ID must name an imported destination.
Test messages remain explicit and have durable check history. `/api/chat/link`
revokes the operator's confirmed links and outstanding challenges.

## Preview and execute rollback

```powershell
$rollback = ./scripts/migrate-chat-plugin.ps1 -Action RollbackPreview -WebSession $session
$rollback | ConvertTo-Json -Depth 10
$returned = ./scripts/migrate-chat-plugin.ps1 -Action Rollback -WebSession $session `
  -ExpectedRevision $rollback.revision

# If the preview identifies unsupported subscriptions, explicitly retain and pause
# those routes while returning only the remaining legacy-compatible routes:
# ./scripts/migrate-chat-plugin.ps1 -Action Rollback -WebSession $session `
#   -ExpectedRevision $rollback.revision -PauseUnsupported
```

Rollback drains plugin work, stops and awaits receivers, completes already accepted
inbox work and pending acknowledgements, and verifies the final state again. It
transfers the latest cursor, including updates accepted after cutover. It never
resets to the import cursor. Known sent messages remain sent. Original resolving
messages must finish their edits before returning to the legacy engine.

The preview refuses a lossless claim for Slack, additional plugin subscriptions,
retargeted imports, externally visible multipart messages, new plugin action
controls, unrepresentable dedupe keys, or events without a legacy producer key. With
`PauseUnsupported`, affected subscriptions are paused and their messages stay in
the plugin tables. This can pause an imported parent if new plugin-only messages
now share that route. Other compatible routes return to the legacy engine. Paused
parents and unrelated future parents remain off. Unknown send outcomes, outstanding
edits and unsettled inbox work block rollback until repaired or settled; the pause
flag does not override those blockers.

The restored legacy path uses the current connection credential, including a
protected-store rotation. Restart also reloads that reference. If it becomes
unavailable, the legacy path stays idle with a diagnostic instead of falling back
to a stale token. A later cutover again takes the maximum durable cursor.

## Retire compatibility

After the rollback window and operator review, explicitly run
`-Action RetireCompatibility -ExpectedRevision <current revision>`. This retires
old message controls and compatibility endpoints and disables the rollback command.
It keeps the migrated connection, destinations, verified operator links and
permanent Host alerts active. New plugin controls continue to use normal settings.

Phase 106 adds `chat_migration_state` and `chat_migration_items` to the control DB.
They and the imported metadata/history are retained for a later explicit cleanup
release, including after compatibility retirement; they are never removed during
rollback. Existing content retention still removes old message text. Do not drop
these additive tables during the compatibility period or its minimum 90-day
history window.