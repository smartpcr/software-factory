# Slack setup for the Chat plugin

Stage 4 supports internal Slack workspace installations through Socket Mode.
Use the [versioned manifest](slack-app-manifest.yaml). Stage 5 provides guided
connection and repair screens under Settings → Integrations → Chat; follow the
[current setup guide](chat-plugin-setup.md). The authenticated configuration,
validation, linking, health and retry APIs below remain available. Live-workspace verification remains
the separate integration gate in the [implementation plan](chat-plugin-implementation-plan.md).

## Create and install the app

1. Create a Slack app from the manifest in the chosen workspace. Record its app ID
   (`A…`) from Basic Information and the workspace ID (`T…`). Obtain administrator
   approval when required by the workspace.
2. Install the app and retain its **bot token** (`xoxb-…`). Enable Socket Mode and
   generate an **app-level token** (`xapp-…`) with `connections:write`. These are
   separate credentials; a user token cannot replace either one.
3. Invite the bot to each selected public channel. The default manifest requests
   `chat:write`, `channels:read`, `channels:history`, and `message.channels` events.
4. For private channels, uncomment `groups:read`, `groups:history` and
   `message.groups`, reinstall the app, invite the bot to the private channel,
   and set `includePrivateChannels` to `true` in Forge. A channel must be visible
   to the bot and the bot must be a member to appear in the picker.

No public callback URL, direct-message, user-profile, slash-command or auto-join
permission is needed. Commands are ordinary channel messages beginning with
`!forge`, delivered through message events. This avoids Slack's slash-command
registration requirement. Organization-wide enterprise installations and Slack
Connect channels/actions are outside this version.

Sources: [Socket Mode and app tokens](https://docs.slack.dev/apis/events-api/using-socket-mode/),
[channel discovery](https://docs.slack.dev/reference/methods/conversations.list/),
[public events](https://docs.slack.dev/reference/events/message.channels/),
[private events](https://docs.slack.dev/reference/events/message.groups/).

## Configure Forge

Select `Forge:Chat:Engine=plugins`, `Forge:Chat:Enabled=true`, and
`Forge:Chat:TelegramSlice:Enabled=false`. Engine selection requires a host refresh;
connection and subscription revisions apply live. Do not restart a host running
agent loops without the host operator's approval.

Store credentials through the host's `IChatSecretStore`. The Windows provider is
`dpapi`, rooted at `<Forge profile>/chat-secrets`; configuration contains only
references, for example `{ "store": "dpapi", "key": "slack.T1.bot" }`. The shared
app credential should use one reference, such as `slack.A1.app`, across all its
workspace connections. Stage 5 supplies credential-entry UI. Existing controlled
fixtures can also use the externally managed `environment` store with keys
beginning `FORGE_CHAT_TEST_`; those variables must exist in the host process.
Never paste credentials into tracked JSON, command bodies, connection settings,
messages, validation output or source control.

All write APIs require an enrolled, authenticated Forge actor and the existing
session/CSRF protection. Host administrators manage connections, credentials and
product grants; product owners manage their parent subscriptions.

Example body for `PUT /api/chat/connections/slack-engineering`:

```json
{
  "expectedRevision": 0,
  "providerId": "slack",
  "installationId": "T_WORKSPACE_ID",
  "displayName": "Slack engineering",
  "enabled": true,
  "settingsVersion": 1,
  "settings": { "appId": "A_APP_ID", "includePrivateChannels": false },
  "secretReferences": {
    "bot-token": { "store": "dpapi", "key": "slack.T_WORKSPACE_ID.bot" },
    "app-token": { "store": "dpapi", "key": "slack.A_APP_ID.app" }
  },
  "productIds": ["PRODUCT_ID"]
}
```

Replace placeholders with real Slack IDs (letters/digits, no underscores). Use
the current revision for subsequent edits. Workspace identity is immutable; a
different workspace needs its own connection and bot credential. Connections for
one app share one receiver. Do not run another process consuming the same app's
Socket Mode traffic: Slack distributes events across sockets without workspace
affinity.

| Step | API and behavior |
|---|---|
| Inspect providers | `GET /api/chat/providers` exposes Slack capabilities and both credential requirements. |
| Verify credentials and discover channels | `GET /api/chat/connections/{id}/discovery?cursor=…` validates bot/workspace identity with `auth.test` and verifies the Socket Mode `hello` app ID. Follow every nonempty cursor, including an empty filtered page. |
| Save destination | `PUT /api/chat/connections/{id}/destinations/{destinationId}` with `expectedRevision`, `externalTenantId`, `channelId`, `displayName`, and `kind` (`public-channel` or `private-channel`). |
| Validate membership | `POST /api/chat/connections/{id}/destinations/{destinationId}/validate`. Checks current channel access and archive/membership state. No test message is sent. |
| Enable a parent | `PUT /api/products/{productId}/story-spawns/{spawnId}/plugins/chat`, then its `/subscriptions/{subscriptionId}` endpoint with the destination, `conversationPolicy: "thread"`, current `parentRevision`, and explicit `interactive: true`. |
| Permit question content | Set product content policy and subscription `contentMode` to `redacted`. Metadata mode directs answers to Forge. |
| Observe health | `GET /api/chat/host/status` includes connection health, receiver diagnostics, delivery failures and reply failures. |

Bot and app authentication are checked separately. `auth.test` identifies the
workspace and bot user; it does not generally return the bot's app ID. Inbound
Events API authorizations must match that bot user and workspace in addition to
the verified app handshake. A mismatched installation is diagnosed and never
dispatched. This avoids adding the `users:read` scope solely for `bots.info`.
[Authentication response](https://docs.slack.dev/reference/methods/auth.test/),
[bot information and required scope](https://docs.slack.dev/reference/methods/bots.info/).

## Link an operator and answer questions

While signed into Forge as an enrolled operator, request
`POST /api/chat/connections/{id}/link-challenge` with
`{ "externalTenantId": "T…", "externalUserId": "U…" }`.
Use your Slack member ID, not the bot ID. Send the returned `channelCommand`
(`!forge link <challenge>`) in a selected channel within two minutes. The
challenge is bound to that sender, workspace, Forge actor and connection revision;
the durable inbox stores its hash. The bot confirms linkage in the channel.
`!forge link` without a challenge returns instructions and the sender/workspace IDs.

| Channel or thread message | Result |
|---|---|
| `!forge unlink` | Revoke the linked operator's chat identities. |
| `!forge status` | Read authorized parent status in its thread. |
| `!forge questions` | List current question message IDs. |
| `!forge answer <message ID> <answer>` | Answer that exact question copy in the current thread. Preserve the decimal timestamp as text. |
| `!forge pause` / `!forge resume` | Apply the shared parent scheduling action with current Forge permissions. |
| Choice button | Submit the stored choice through the shared domain action core. |
| Custom answer button, then thread text | Select that question for the clicking operator for ten minutes. Several active selections require an explicit message ID. |

Slack thread replies reference the root, so replying visually under a question
does not select it when several questions are open. Forge asks for a choice and
never guesses a question. Use a button or an explicit message ID. Buttons carry
opaque lookup keys; they grant no permission themselves.

One root thread covers a parent and all its children for each destination and
activation. Each split message retains its own timestamp; buttons are on the last
part. Resolution removes controls across all destinations, including Telegram.
Retirement updates the root and records local retirement; Slack has no close-thread
operation. Normal reception uses events, with no `conversations.history` or
`conversations.replies` polling.
[Thread posting and accessibility](https://docs.slack.dev/reference/methods/chat.postMessage/),
[message updates](https://docs.slack.dev/reference/methods/chat.update/).

## Diagnose and repair

Failures contain fixed codes and never raw Slack responses, credentials or socket
tickets. The APIs expose the following repair states for Stage 5's connection UI:

| Code | Repair |
|---|---|
| `slack-bot-token-required` / `slack-bot-token-revoked` | Store a valid installed bot credential; update its reference/revision and validate again. |
| `slack-app-token-required` / `slack-app-token-revoked` / `slack-app-scope-required` | Supply the app-level token with `connections:write`. |
| `slack-app-token-conflict` | All workspace members of one receiver must use the same app credential. Disable those connections, replace the shared reference, update revisions and re-enable them. |
| `slack-app-handshake-mismatch` | Match the configured app ID to the app that issued the app token. |
| `slack-app-installation-mismatch` | Install the configured app in this workspace and supply its bot credential; inspect receiver diagnostics. |
| `slack-workspace-mismatch` / `slack-workspace-unavailable` | Correct the installation/token pairing or restore workspace access. |
| `slack-bot-scope-required` | Add the selected scopes, reinstall the app, and validate again. |
| `slack-membership-required` | Invite the bot back to the selected channel. |
| `slack-channel-archived` / `slack-channel-unavailable` | Restore the channel or select another accessible channel/destination. |
| `slack-private-channels-disabled` | Enable the private option, its scopes and its event subscription together. |
| `slack-socket-disabled` | Enable Socket Mode in app settings. |
| `slack-rate-limited` | Wait for the recorded retry time. Other workspaces continue independently. |
| `slack-unknown-workspace` | Add an authorized workspace connection or remove the unused installation. The envelope is diagnosed durably before acknowledgement, without a domain action. |

After access is restored, validate the saved destination and explicitly retry a
failed delivery using `POST /api/chat/deliveries/{id}/retry`. Known roots are reused.
Unknown creation/send outcomes remain uncertain for operator inspection; retries
must not silently create duplicate roots or messages. Disabling one workspace
detaches it while other workspaces continue on the same socket.

Requests are paced per workspace/method and channel; HTTP 429 `Retry-After` hints
delay that workspace's method. Socket reconnects reacquire a fresh URL and verify
the handshake again, with bounded exponential backoff and cancellation. A failed
inbox commit is not acknowledged. Domain dispatch runs after durable acceptance.
[Rate limits](https://docs.slack.dev/apis/web-api/rate-limits/),
[interaction acknowledgement](https://docs.slack.dev/interactivity/handling-user-interaction/).
