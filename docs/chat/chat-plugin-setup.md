# Chat plugin setup

Stage 5 provides **Settings → Chat** and a **Chat** control on every
Workstreams row, including completed rows. The [validation report](chat-plugin-stage5-validation.md)
records automated evidence and the remaining live-provider gates.
Stage 6 adds [legacy migration and rollback](chat-plugin-migration.md); use that
guide when importing an existing bridge instead of adding a duplicate bot connection.

The full Settings page has separate **Chat**, **Repository providers**, and
**General** tabs. Repository providers contains Azure DevOps and GitHub; General
contains collapsible Browser session and Maintenance sections. The selected tab
survives refresh. The fixed header keeps **Close** available while a single scrollbar
reaches the bottom of each tab. Close returns to the previous view, or Workstreams
when Settings was opened directly.

Chat settings now separates **Saved connections** on the left from **Add a
connection** and selected-channel setup on the right. Select a saved connection
to manage its channels, or choose Telegram/Slack to open a new setup. Repair actions
stay aligned within the list. Account linking and collapsible Host alerts sit below;
the layout stacks on narrow screens.

## In-app walkthrough

Open the Chat icon after Pause and Delete on a Workstreams row, choose a provider,
and select **Set up connection**. Alternatively, use **Settings → Chat → Add Slack connection / Add Telegram connection**.

Select **Connect with Slack** or **Connect with Telegram** to authorize the connection
in the provider's own window. Review the returned workspace/bot, choose product
access, then save. IDs and credentials are filled in automatically. An unregistered
Telegram host opens **Enable Telegram connections**: open BotFather, enable Bot
Management Mode on a dedicated bot, and paste its initial token once. **Verify and
continue** saves it encrypted and opens Telegram approval immediately. Later
connections skip this setup. See [one-time registration](chat-connection-authorization.md)
for provider prerequisites. On an unregistered Slack host, the new **Approve Slack
setup** popup copies an approval request and opens Slack. Paste it into a Slack
conversation, confirm the private permission dialog, then return its confirmation
code. Forge creates the app, configures Socket Mode and retrieves both credentials.
No manual app registration or HTTPS callback is needed for that path. The latest
Slack endpoints and the CLI header correction are deployed. Real approval-ticket
creation and the in-app modal pass live checks. Assisted workspace consent, installation and encrypted save are recorded in
[live setup evidence](chat-slack-live-setup.md); the full browser-only flow and
parent delivery remain release gates. The channel transport fix is deployed after
approval; the actual Settings UI validates `#all-forge` successfully. Use
[Telegram setup](telegram-setup.md), [Slack setup](slack-setup.md),
[troubleshooting](chat-plugin-troubleshooting.md) and [API examples](chat-plugin-api-examples.md)
for current operator procedures.

For a new Slack workspace, choose **Create a new Slack workspace** inside that
popup. Complete signup and any account verification in Slack, then select
**Workspace ready, continue** in Forge. Forge renews the approval request so the
time spent creating the workspace does not leave you with an expired request.
Use **Find my workspaces** when an existing workspace is linked to your email.

For an existing app/bot, expand **Advanced · use existing credentials → Set up
manually**, then **Step-by-step Slack setup** or **Step-by-step Telegram setup**.
Each numbered step opens independently. The Slack guide
shows the app settings and highlighted bot-token field, explains the workspace/app
IDs and both tokens, and offers **Copy Slack manifest** and **Download manifest**.
Select a screenshot to enlarge it. The Telegram guide covers BotFather, the bot ID
and full token with an illustration, and group/Topics permissions. **Finish in Forge**
explains saving, choosing a channel, verification and account linking.

Connection details, credentials and product access are separate collapsible sections.
In the row drawer, the body scrolls while **Close**, **Save connection** and **Cancel
connection setup** stay visible. Cancel setup returns to the original row draft;
Close exits the drawer. Standalone connection setup in Chat settings also has Close.

## Connections and channels

1. Use an enrolled, paired Forge operator. Host administrators configure connections
   and Host alerts; product owners configure their own rows. These permissions are separate.
2. Add a Telegram or Slack connection in Chat settings and approve it in the provider
   window. Review its verified name/identity and select product grants. For existing
   credentials use the advanced manual form and [Slack setup guide](slack-setup.md).
3. Save the approved connection. In the manual form, leave Enable unchecked to persist incomplete setup.
   Protected credentials are write-only. External references remain managed by their
   named store; process environment changes require a host refresh.
4. Discover a channel by name or enter its ID. Telegram forum groups require Topics
   and bot permission to manage topics. Save records the channel and validates access.
5. Use **Send test message** deliberately, then reply `check` to that message within
   ten minutes. Outbound success and the verified inbound reply are separate results.
6. Open **Your chat identity → Link or re-link my account** and choose the connection.
   Forge fills in the installation and reuses a previously verified account ID when
   unambiguous. For a first Telegram identity, the current form requires the personal
   numeric user ID; a username or bot ID cannot substitute for it. Confirm ownership,
   then select **Link my Telegram account** and press **Start** in Telegram. Forge
   checks completion automatically. If the browser blocks the popup, use **Open
   Telegram confirmation**. Updated Slack linking selects a saved channel and
   requests a private **Confirm link** button; select **Link my Slack account**,
   then approve in Slack. It checks completion automatically, with a channel link
   and fresh-request fallback in its dialog. This update is deployed in API PID
   43620; Settings detects older hosts and explains the pending update. Linking
   requires an active plugin receiver, remains scoped to the selected installation,
   and does not grant new product permissions. Revoke/re-link is available here.

The later Slack member-ID recovery correction restores a previous entry for the
same operator, connection and workspace when retained request history is available.
It leaves ownership unchecked and requires provider approval. This correction
awaits a new approved backend restart; the private-button flow is already deployed.

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

To start host alerts, open **Settings → Chat → Host alerts → Configure host alerts**,
choose a provider and saved channel, review the event filters/content policy, then
select **Save host alerts**. The form opens a draft; selecting a channel alone does
not save it. Host subscriptions are separate from every story's destinations.
Personal account linking is required for work actions, not for these notifications.
The **Unrouted** number includes historical filtered, unresolved and terminal records;
it is not a count of messages queued for retry. See [routing history explained](chat-plugin-troubleshooting.md#host-alerts-shows-unrouted).

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
| Slack private approval | `GET /api/chat/connections/{id}/link-destinations`; `POST` / `DELETE /api/chat/connections/{id}/link-confirmation` |
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