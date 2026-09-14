# Chat configuration API examples

These requests mirror the UI. Prefer the atomic parent `/setup` endpoint to
separate plugin/subscription writes. Example IDs are placeholders; resolve real
canonical product and parent-spawn IDs first. A title, child ID, web alias or
`productId=all` cannot stand in for an authorized parent scope.

## Authentication and revisions

Use an enrolled Forge operator's paired browser session. Mutations require its
cookie, allowed browser origin and `X-Forge-CSRF` token. In the UI these are supplied
by the shared API client; scripts should use the existing paired
`WebRequestSession`/pairing-code helpers. Never invent an actor through a header.
Host administrators manage connections, credentials and host alerts; product
owners manage their parent routes. A personal chat link grants no extra rights.

Read before writing. Creation uses `expectedRevision: 0`; updates use the returned
current revision. HTTP 409 means reload/review, not blind retry. Bodies below are
JSON; credentials are represented only by protected references.

## Save and validate a destination

After saving a connection using [Telegram](telegram-setup.md#connection-api-example)
or [Slack](slack-setup.md#configure-forge), read
`GET /api/chat/connections/CONNECTION_ID/discovery?cursor=OPAQUE_CURSOR`. Omit the
cursor for the first page; URL-encode later cursors and follow even an empty page
when `nextCursor` is nonempty.

`PUT /api/chat/connections/CONNECTION_ID/destinations/DESTINATION_ID`:

```json
{
  "expectedRevision": 0,
  "externalTenantId": "T012345",
  "channelId": "C012345",
  "displayName": "forge-test",
  "kind": "public-channel"
}
```

For Telegram, use the bot ID as `externalTenantId`, the numeric group ID as
`channelId` and `kind: "forum"` for Topics. Then call
`POST /api/chat/connections/CONNECTION_ID/destinations/DESTINATION_ID/validate`
without a body. Validation performs access checks but sends no test message.

## Save all parent destinations atomically

Read `GET /api/products/PRODUCT_ID/story-spawns/PARENT_ID/plugins/chat/setup`.
The response wraps configuration in `plugin`; take `plugin.revision` for this PUT.
Use destination IDs already saved and granted to this product.

`PUT /api/products/PRODUCT_ID/story-spawns/PARENT_ID/plugins/chat/setup`:

```json
{
  "expectedRevision": 0,
  "enabled": true,
  "destinations": [
    {
      "destinationId": "TELEGRAM_DESTINATION_ID",
      "conversationPolicy": "topic",
      "interactive": true,
      "contentMode": "metadata",
      "eventKinds": ["open-questions", "workstream-stalled"],
      "paused": false
    },
    {
      "destinationId": "SLACK_DESTINATION_ID",
      "conversationPolicy": "thread",
      "interactive": true,
      "contentMode": "metadata",
      "eventKinds": ["open-questions", "workstream-stalled"],
      "paused": false
    }
  ],
  "activateNewRun": false
}
```

The full list replaces the parent's routes; omitting a destination retires its
subscription. Save queues introductions and covers verified current/future children.
Metadata mode directs question answers to Forge. To allow redacted question text
and choices, select `contentMode: "redacted"` only after the host explicitly allows
that product. Set `interactive: false` for notifications only. Enabling a completed
parent does not restart it. Reusing a parent ID for a new run requires explicit
`activateNewRun: true` after reviewing that new run.

## Replace a credential

`PUT /api/chat/connections/CONNECTION_ID/credentials/bot-token` accepts
`{ "expectedRevision": CURRENT_REVISION, "value": REPLACEMENT_FROM_PROTECTED_INPUT }`.
This is a schema example, not pasteable JSON. Collect the value in a password field
or secure prompt; do not place it in tracked files, command arguments or transcripts.

The response is the updated connection: revision increments and `secretReferences`
contains the new write-only protected reference. It never returns the secret value.
Disable the affected connection first, replace the credential, enable it and
revalidate. Do not send an old connection object after the credential write or it
can restore the old reference. A shared Slack app token uses the credential name
`app-token`; rotate it once and point all disabled connections for that app at the
returned reference before re-enabling. See [Slack rotation](slack-setup.md#rotate-credentials).

## Preview, explicit transport checks and history

`POST /api/chat/preview` with
`{ "productId": "PRODUCT_ID", "contentMode": "redacted", "text": "A test question" }`
returns the effective redacted/metadata preview without posting.

Only on a destination approved for a real test, use
`POST /api/chat/connections/CONNECTION_ID/destinations/DESTINATION_ID/checks`:

```json
{
  "requestId": "6a866cdb-40e5-4b71-a4bb-b2a7fb7b2170",
  "connectionRevision": 1,
  "destinationRevision": 1,
  "text": "Forge release transport check"
}
```

Use actual current revisions and a fresh UUID for a new check. Reuse its request
ID only to retrieve the same attempt after a response failure. Reply `check` to
the actual provider message within ten minutes. `GET` on the same `/checks` path
reports outbound and inbound state separately; an uncertain check is not replayed.

| Read/action | Endpoint |
|---|---|
| Available permissions and own links | `GET /api/chat/management` |
| Saved and applied connection revisions | `GET /api/chat/connections/CONNECTION_ID/health` |
| Parent deliveries | `GET /api/products/PRODUCT_ID/story-spawns/PARENT_ID/plugins/chat/history` |
| Batched row status | `GET /api/products/PRODUCT_ID/chat/summaries` |
| Host unrouted/orphaned scopes | `GET /api/chat/host/status` |
| Retry one known failed delivery | `POST /api/chat/deliveries/DELIVERY_ID/retry` |
| Revoke own chat identities and outstanding proofs | `DELETE /api/chat/operator-links/self` |
| List channels available for own Slack identity confirmation | `GET /api/chat/connections/CONNECTION_ID/link-destinations` |
| Request/cancel a private Slack account confirmation | `POST` / `DELETE /api/chat/connections/CONNECTION_ID/link-confirmation` |

The private Slack confirmation endpoint was deployed on September 12 at 09:41 UTC
in API PID 43620. Check `slackLinkConfirmationAvailable` in management before using it.
The later member-ID recovery change adds `recentLinkTargets` to management, with
`connectionId`, `externalTenantId` and `externalUserId`. These are only the current
operator's previous inputs for accessible Slack connections, never verified links.
This field is optional for older-host compatibility and was deployed after
approval at 21:23 UTC in API PID 36144. Only `links` indicates completed personal
proof. The private-callback repair was deployed at 21:41 UTC in API PID 35552,
and an actual user approval now produces a verified link; see the
[release record](chat-plugin-release-gate.md#slack-private-confirmation-callback-repair-2026-09-12).

POST a saved destination and your personal Slack user ID:

```json
{
  "destinationId": "SAVED_DESTINATION_ID",
  "externalUserId": "U0123456789"
}
```

The response contains `expiresInSeconds`, `deepLink` and `channelName`; it contains
no challenge token. Open the channel and approve the private button from that
account. Poll management's own links for completion. Sending the request alone
does not link anything. DELETE cancels this operator's pending requests for the
connection. Product operators can use connections granted to products they can
read; host administrators can use all configured connections.

Migration, cutover and rollback have their own revisions, ownership checks and
preview procedure; use the [migration API helper](chat-plugin-migration.md).