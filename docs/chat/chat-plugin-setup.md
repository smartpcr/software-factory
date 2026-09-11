# Chat plugin setup

Stage 5 provides **Settings → Integrations → Chat** and a **Chat** control on every
Workstreams row, including completed rows. The [validation report](chat-plugin-stage5-validation.md)
records automated evidence and the remaining live-provider gates.
Stage 6 adds [legacy migration and rollback](chat-plugin-migration.md); use that
guide when importing an existing bridge instead of adding a duplicate bot connection.

## Connections and channels

1. Use an enrolled, paired Forge operator. Host administrators configure connections
   and Host alerts; product owners configure their own rows. These permissions are separate.
2. Add a Telegram or Slack connection in Chat settings. Enter its name, bot/workspace
   identity, credentials and product grants. Slack also requires the app ID and an
   app token; follow the [Slack manifest/setup guide](slack-setup.md).
3. Enable the connection and save. Leave it disabled to persist incomplete setup.
   Protected credentials are write-only. External references remain managed by their
   named store; process environment changes require a host refresh.
4. Discover a channel by name or enter its ID. Telegram forum groups require Topics
   and bot permission to manage topics. Save records the channel and validates access.
5. Use **Send test message** deliberately, then reply `check` to that message within
   ten minutes. Outbound success and the verified inbound reply are separate results.
6. Link your own chat identity, confirm the displayed installation/user tuple, and
   redeem the short-lived challenge from that chat account. Link status and product
   authority remain independent of transport health. Revoke/re-link is available here.

Use **Repair / edit** to replace credentials or change product grants. Recheck channel
access after restoring Slack membership or unarchiving a channel. Connections sharing
a Slack app must share its app credential; disable the affected connections during a
shared app credential rotation, update them together, then enable them. A failed
revision check preserves the draft; **Reload saved connection** explicitly loads the
committed version before further editing.

## Parent rows and Host alerts

On the desired row, use **Enable chat remote control → Provider → Channel → Save and
enable**. Interactive mode is the default; notification-only is explicit. Save queues
one introduction/conversation per destination. Current and future children share it.
Adding Chat to a completed row does not restart the row or its agents. Closing the
drawer discards row changes; connections/channels explicitly saved inline remain
available. Copy destinations fills a draft and still requires Save.

Each destination has a content policy and delivery options. Metadata is the default.
Redacted question content also requires the host's explicit product-content opt-in;
otherwise the effective output remains metadata and the UI explains the host refresh
needed. Preview does not send. Pausing destination delivery does not pause work.

Permanent Host alerts default to `queue-strain` and `human-interaction`, with optional
unresolved work events, unrouted counts and orphaned scope information. They retain a
separate host permission. Retiring a subscription stops its routing/control authority
and leaves provider-held messages in place. Delivery history shows safe failure
reasons and retry timing; only a known failed delivery offers Retry. Unknown external
outcomes are not automatically replayed.

## API and helper paths

All mutations require a paired operator session and `X-Forge-CSRF`. Product operations
resolve and authorize their actual product and parent IDs. Credential bodies must not
be logged or stored in shell history. Expected revisions reject stale writes.

| Operation | Endpoint |
|---|---|
| Permissions, products, self-links, secret-store metadata | `GET /api/chat/management` |
| Connection configuration | `GET /api/chat/connections`; `GET/PUT/DELETE /api/chat/connections/{id}` |
| Protected credential replacement | `PUT /api/chat/connections/{id}/credentials/{name}` with expected revision and value |
| Saved destinations | `GET /api/chat/connections/{id}/destinations`; `PUT/DELETE .../destinations/{destinationId}` |
| Channel discovery/access | `GET .../discovery?cursor=...`; `POST .../destinations/{destinationId}/validate` |
| Explicit send and reply-check history | `POST/GET .../destinations/{destinationId}/checks`; POST includes a request UUID and both revisions |
| Connection health | `GET /api/chat/connections/{id}/health` |
| Begin and revoke self-link | `POST /api/chat/connections/{id}/link-challenge`; `DELETE /api/chat/operator-links/self` |
| Atomic row setup | `GET/PUT /api/products/{productId}/story-spawns/{spawnId}/plugins/chat/setup` |
| Batched summaries | `GET /api/products/{productId}/chat/summaries` |
| Row delivery history | `GET /api/products/{productId}/story-spawns/{spawnId}/plugins/chat/history` |
| Host scope | `GET /api/chat/host/status`; `GET /api/chat/host/subscriptions`; `PUT/DELETE .../subscriptions/{id}` |
| Content preview | `POST /api/chat/preview` |
| Explicit failed delivery retry | `POST /api/chat/deliveries/{id}/retry` |

`scripts/setup-telegram-bridge.ps1` calls these APIs. Supply an existing paired
`WebRequestSession` or a one-use Forge browser pairing code:

```powershell
$pairing = Read-Host 'One-use Forge pairing code' -AsSecureString
./scripts/setup-telegram-bridge.ps1 -BotId '12345' -ChannelId '-100123' `
  -ChannelName 'Forge Ops' -ProductIds 'your-product-id' -PairingCode $pairing
```

The helper prompts for a hidden bot token unless `-UseSavedCredential` is supplied.
`-SpawnId` optionally enables a parent (exactly one product ID is then required).
`-NotificationOnly` selects that alternative. A test message requires `-SendTest`.
The former `-WriteConfig`, `-StoreToken`, `-SkipSmokeTest`, desktop installation and
legacy sign-in parameters are retired from normal setup. The helper never patches
configuration files or starts a second provider receiver.

## Host activation

The default engine remains `legacy`. Plugin runtime activation requires
`Forge:Chat:Engine=plugins`, `Forge:Chat:Enabled=true`, and
`Forge:Chat:TelegramSlice:Enabled=false`. Host and per-product content ceilings remain
host configuration. Engine/environment/ceiling changes take effect after a host
refresh; saved connection/subscription revisions apply live once this backend is
loaded. The running host must be restarted only at an operator-approved time.
For an existing bridge, Stage 6 persists the selected engine in the control DB and
loads it before either engine runs. Explicit cutover/rollback applies live after this
backend is loaded; the persisted owner takes precedence over the startup engine
selection. `Enabled=false` still stops both engines. A migration opts in when
`Enabled` is absent. Use the migration command for rollback, preserving the latest
cursor and compatible delivery states.