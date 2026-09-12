# Chat troubleshooting

Start in **Settings → Chat**. Select the saved connection and inspect its channel
access, delivery checks and history. **Configured** means settings exist; it does
not prove channel membership, successful delivery, a verified reply or a personal
identity link. These are separate checks. The [release report](chat-plugin-release-gate.md)
records which live behaviors have actually been verified.

## Setup and authorization

| Symptom | What to do |
|---|---|
| Add/repair controls unavailable | Use a paired Forge host administrator for connections. Product ownership alone permits parent setup, not host credential changes. Inspect `GET /api/chat/management`. |
| Legacy-engine or host-disabled notice | Review the effective engine/master switch. Existing legacy hosts use the [migration preview and cutover](chat-plugin-migration.md). Configuration can be saved before activation. |
| Telegram manager setup appears | Follow [Telegram step 1](telegram-setup.md#1-connect-a-bot). Select the dedicated manager in BotFather's Mini App; enable Bot Management Mode. |
| Telegram opens without a confirmation | Use the explicit confirmation link in Forge, then **Start** in the selected bot conversation. Request a fresh proof if expired. A Telegram account login message is unrelated. |
| Telegram identity ID rejected | Supply your personal numeric sender ID, not a username, group ID or bot ID. Verified IDs can be reused; first-time discovery is not automated. |
| Slack workspace not found or inaccessible | Use **Find my workspaces**, or **Create a new Slack workspace** if you need one you can administer. After signup select **Workspace ready, continue** for a fresh request. |
| Slack approval window is blocked | Use **Open Slack if no window appeared**. If clipboard permission fails, copy the displayed request manually. Paste it into a writable conversation, not Slack's read-only notification app. |
| Slackbot rejects an approval ticket | Refresh the request in Forge and use that new request once. The code must come from its private permission dialog. Do not keep submitting an expired ticket. |
| `authorization-slack-admin-approval-required` | The workspace requires an administrator. Forge does not bypass that policy or submit a request on your behalf. |
| `authorization-installation-unsupported` or uncertain app creation | Inspect the returned app reference and the selected installation. Preserve the failure details; don't repeatedly create apps. An earlier live endpoint failure remains unproven; [assisted installation evidence](chat-slack-live-setup.md) is not proof that every browser-only step passed. |
| `revision-conflict` | Reload the saved configuration, review another user's changes and save against its revision. Do not overwrite with a guessed revision. |

## Channel access and reception

| Symptom/code | What to do |
|---|---|
| Telegram channel list empty | Discovery uses chats observed by the active receiver. Add the connection bot to the group and generate an intended group message, or enter the chat ID. Do not start a second poller. |
| `telegram-manage-topics-required` | Enable Topics on the group and grant this connection bot Manage Topics as an administrator. |
| `telegram-webhook-conflict` / `telegram-receiver-conflict` | The bot already belongs to another consumer. Resolve ownership or use a dedicated bot. Forge does not silently delete a webhook. |
| `telegram-token-revoked` / `telegram-membership-denied` | Replace the token or restore the bot's membership/posting permission, then validate again. |
| Slack channel missing / `slack-membership-required` | Add the installed Forge app to that channel. Discovery excludes channels the bot has not joined. |
| `slack-private-channels-disabled` / `slack-bot-scope-required` | Align the private setting, both `groups:*` scopes and `message.groups`, reinstall and invite the bot. See [exact scopes](slack-setup.md#exact-permissions). |
| `slack-app-token-conflict` | All connections for one app must use the same app token. Follow the coordinated rotation procedure; do not overwrite a shared blob. |
| `slack-app-handshake-mismatch` / `slack-workspace-mismatch` | Match the app token to the app ID and the bot token to the workspace. These are distinct credentials. |
| `slack-payload-invalid` during channel validation | Use a build containing the conversation form-encoding fix. On the recorded host, JSON POST caused `missing required field: channel`; form encoding succeeded. The approved September 12 deployment fixes this: select the saved channel and **Validate channel access**. The recorded `#all-forge` destination now reports `ready`. |
| Send succeeds, reply stays pending | Check the receiver, intended thread/topic, reply text `check`, ten-minute expiry and sender identity. Outbound success alone cannot verify reception. |
| Saved revision exceeds applied revision | Wait for the configured runtime refresh interval, then inspect health/receiver errors. Protected credential edits apply live; host process environment and content-policy changes need an approved refresh. |

## Routing and actions

One subscription belongs to one `(productId, parent spawnId, destination)`.
Select the parent row when configuring Chat; child controls are not independent.
Two parents in one channel get different conversations. Turning Chat off removes
that route's action authority without pausing work. Host alerts have separate
host permissions and remain available when no story is active.

For multiple open questions, choose a question button or use an explicit provider
message ID. A stale button cannot overwrite an answer accepted elsewhere. If a
linked user loses Forge product access, old controls are denied too. Changing
content mode cannot bypass the host's product-content allowance.

Zero matching subscriptions produces an **unrouted** record. Enabling a route later
does not replay old events. Failed, known delivery attempts can be explicitly
retried; **unknown outcome** means the provider may already have received it.
Inspect the provider and retained message references before repair or resend.
Deleting a subscription leaves provider-held messages in place.

## Configured connection shows Needs linking

**Configured** describes the saved bot/app connection. **Needs linking** in
**Your chat identity** describes your personal account in that installation.
Installing the app or linking a different Telegram bot does not establish this
identity. Notifications can be delivered without a personal link; work commands
and answers need a verified account with current Forge product permissions.

The updated Slack UI uses a private approval button. Its backend was deployed
after approval on September 12 at 09:41 UTC (API PID 43620); the actual Settings
form and API capability pass. If the old command form is still visible, refresh
Settings. On an older backend, Settings shows a host-update notice.

If **My chat user ID** is blank despite a previous attempt, the deployed form only
reuses verified identities. A correction restores the operator's last entered ID
for the same connection/workspace while request history is retained. It awaits a
new approved backend restart. Until then, enter the same personal member ID again;
the private confirmation button already works. A remembered ID is not proof.

To link your account:

1. Expand **Your chat identity → Link or re-link my account**.
2. Select the saved Slack connection, such as **forge**. **Workspace or bot ID**
   fills with that installation's workspace ID automatically.
3. Enter your personal Slack member ID in **My chat user ID**, then check
   **I confirm this is my own chat account**. A display name or bot ID cannot
   establish your personal identity.
4. Choose **Confirmation channel**, then **Link my Slack account**. A sole saved
   channel is selected automatically. Forge opens that channel and requests a
   private confirmation message for your account.
5. In Slack, select **Confirm link → Link account**. Return to Forge; its dialog
   closes and the badge changes to **Linked** after the verified user callback.
   There is no command or challenge code to copy.

If the browser blocks Slack, use **Open Slack confirmation channel** in the Forge
dialog. If the private message is missing, keep Slack active in that channel and
select **Send a new confirmation**. Slack does not guarantee ephemeral delivery,
and those messages disappear across sessions/reloads. An accepted API request
does not prove you saw a message. A resend invalidates the previous challenge;
**Cancel linking**, expiry, or connection rotation also prevents an old approval.
`slack-link-user-not-in-channel` means your personal account must join the chosen
channel. No additional DM or user-token scopes are needed. The first personal
member ID still needs to be supplied; a bot connection cannot infer which Slack
account belongs to the signed-in Forge user.

The link challenge is separate from the earlier Slack CLI app-installation
confirmation. Do not paste a Slack login code or a previous installation code
into the member-ID field or send it as a link challenge.

## Host alerts shows Unrouted

The **Unrouted** total counts retained events for which no delivery route was
recorded, grouped by their original routing reason. It includes filtered events
and historical records that are already answered, acted, superseded or expired.
It is not the count of messages waiting to retry. Expand **Delivery history** to
see its reason breakdown and the actual delivery states.

| Reason | Meaning and next step |
|---|---|
| `no-destination` / `host-alerts-need-destination` | No host route is configured. Choose a saved destination and save the host-alert form. The second code is the current setup state, not an event-history reason. |
| `filtered` | No eligible subscription accepted the event. Check active routes, selected event filters and connection availability; an intentional filter needs no repair. |
| `ownership-unverified` | Forge could not verify the work's product/parent ownership. Check the product database and migration/ownership diagnostics; do not assign old events to an arbitrary parent. |
| `unresolved-scope` | The originating work could not be resolved to a parent. Review the originating work and any intended host fallback. New host routing does not repair an old ownership mapping. |

To configure future host alerts:

1. Under **Saved connections**, select the intended provider connection. Discover
   or add its channel if needed, then choose **Validate channel access** and check
   for **ready**. App installation alone does not select an alert destination.
2. Open **Host alerts → Configure host alerts**. Choose **Host provider**, the
   connection when there is more than one, and **Host channel**.
3. Review **Host content policy** and **Event filters**. The defaults are metadata
   only, `queue-strain` and `human-interaction`. Optional work events are an explicit
   choice. Host alerts use separate permissions from story configuration.
4. Select **Save host alerts**. Merely choosing the channel leaves a draft.
   Confirm the subscription appears in the saved list. **Cancel alert changes**
   discards the draft. Use an intentional send/reply check to test channel delivery.

Saving affects future events; it does not replay or erase retained history. In the
September 12 host read, **38** consists of 23 `ownership-unverified`, 13
`unresolved-scope` and two `filtered` records. There are no host subscriptions;
the displayed historical deliveries are 30 cancelled, four answered and two acted.
The Slack destination is now ready after deployment, but the user still needs to
select where future host alerts should go. [Live checkpoint](chat-plugin-release-gate.md#current-host-setup-still-required).

## Evidence to collect

Record build/version, UTC time, connection and applied revisions, product/parent/
destination IDs, safe error codes, and delivery/check IDs. Preserve the selected
scope and known message IDs through retry. Do not copy raw provider URLs, tokens,
authorization tickets, link challenges or personal message bodies into logs.
Use [API examples](chat-plugin-api-examples.md) for health, preview, checks and history.

Before rollback, read its preview. Outstanding sends/edits or inbox work must
settle. Slack and new plugin-only routes cannot return to the legacy sender;
the explicit pause option retains their history. The latest durable Telegram
cursor is transferred, never the pre-cutover cursor. See the
[rollback boundary](chat-plugin-migration.md#preview-and-execute-rollback).