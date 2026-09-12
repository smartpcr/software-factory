# Chat connection authorization

The new-connection screen starts with **Connect with Slack** or **Connect with
Telegram** when registration is ready. An unregistered Telegram host instead opens
**Enable Telegram connections**, with an actionable BotFather link and one protected
token field. Provider consent then fills in the bot/workspace identity and credentials.
Review the verified connection, choose its product grants, and save. Then choose a
channel as usual. Existing connections still use **Repair / edit**; the old token
form and illustrated instructions are under **Advanced · use existing credentials**.
The active **Add Telegram connection / Add Slack connection** button stays highlighted
with a checkmark while its new-connection editor is open. An unregistered Slack
host now offers an approval popup and automatic app provisioning. The
Slack endpoints and the CLI identification-header correction are deployed. Real
ticket creation through Forge and the in-app approval dialog pass live checks.
Assisted Slack consent, app installation and encrypted connection save are
verified. The complete browser-only approval/save flow still needs live acceptance.
After the approved September 12 restart, `#all-forge` membership and channel access
are verified through Forge's API and actual Settings UI; the connection is ready
at saved/applied revision 4/4. See [live setup and layout](chat-slack-live-setup.md).

## Provider requirements

Slack can authorize app creation from Forge, or use an existing host-owned OAuth
app. Telegram still needs a dedicated manager registered once. Both paths require
the provider account owner's authorization. Forge does not host a shared public
Slack app or a shared Telegram manager service.

### Slack

#### Automatic app provisioning

On a host without existing Slack authorization settings:

1. Open **Settings → Chat → Add Slack connection**. Choose whether private channels
   are needed, then select **Connect with Slack**. Forge opens **Approve Slack setup**
   in a modal with fixed Close and action buttons and a scrolling body.
   If you need your own workspace, choose **Create a new Slack workspace** there.
   Complete Slack's sign-in/email verification and workspace setup, then return
   to Forge and select **Workspace ready, continue**. This starts a fresh approval
   request even if signup took longer than ten minutes. **Find my workspaces**
   opens Slack sign-in when you want to use an existing workspace.
2. Select **Copy approval request and open Slack**. Sign in to the intended workspace
   if necessary. Paste the copied request into a Slack conversation and press Enter.
   Slack shows a private developer-permission dialog; review it and select **Confirm**.
3. Paste Slack's returned confirmation code into the popup and select **Verify and
   configure automatically**. Forge creates the app, configures bot permissions,
   message events and Socket Mode, installs it, and obtains both bot and app tokens.
4. Review the workspace name, choose the allowed Forge products, and select **Save
   connection**. Forge encrypts both credentials. Invite the new Forge app to the
   intended channel, then discover and validate that channel in Forge.

If a browser blocks the window, **Open Slack if no window appeared** remains visible.
Slack opens immediately, independently of clipboard permission. If copying fails
or remains unconfirmed for two seconds, Forge exposes a read-only request that is
selected when focused for manual copying. Closing the popup allows it to be reopened;
cancelling connection setup discards the pending authorization. An expired request
offers **Refresh approval request**. A code is never a Slack account password
and should come only from the permission dialog for this request.

Workspace creation uses [Slack's official signup](https://slack.com/get-started#/createnew)
and [workspace creation instructions](https://slack.com/help/articles/206845317-Create-a-Slack-workspace).
Slack performs account verification and creates the workspace with the user as its
owner. Its documented [workspace creation API](https://docs.slack.dev/reference/methods/admin.teams.create/)
is for enterprise organizations; this standard signup path does not use that API.
Opening signup alone is not evidence that a workspace exists. Forge resumes app
authorization only when the user returns and selects **Workspace ready, continue**.

This path uses Slack CLI v4.7's developer authorization/provisioning protocol:
`apps.hosted.generateAuthTicket`, `apps.hosted.exchangeAuthTicket`,
`apps.manifest.create`, and `apps.developerInstall`. It requires no CLI installation,
HTTPS callback, pre-existing app, or manual token/ID entry. Forge keeps tooling
credentials and refresh tokens only for the current request; it stores only the
resulting bot and Socket Mode credentials when the reviewed connection is saved.
The approval request and confirmation code still require a human handoff. This is
not the existing OAuth flow and does not bypass workspace installation policy.

An existing workspace connection is rejected before creating another app. An
uncertain creation/install response is never automatically replayed. If Slack
created an app before a later failure, Forge links to that app for review; it does
not delete it. Workspace-admin approval requirements return an explicit error and
do not automatically submit requests to administrators. Cancelling after successful
provisioning also leaves the app in Slack. Enterprise-wide installations remain
unsupported. Both saved token types and the bot's workspace are checked, and the
app token must successfully request a Slack Socket Mode URL; the normal receiver
then verifies the app ID in Slack's socket handshake.

Protocol compatibility is based on Slack's official CLI source at
[`fcb07820d23096758f075ed7a45f5599bab706da`](https://github.com/slackapi/slack-cli/tree/fcb07820d23096758f075ed7a45f5599bab706da),
specifically `internal/api/auth.go`, `internal/api/app.go`, and
`internal/pkg/auth/login.go`. These CLI endpoints can change independently of
ordinary OAuth; fixture validation is not a completed live workspace installation.
See [Slack CLI authorization](https://docs.slack.dev/tools/slack-cli/guides/authorizing-the-slack-cli)
and [app installation](https://docs.slack.dev/tools/slack-cli/reference/commands/slack_app_install/).

#### Existing shared OAuth app

An administrator creates the shared app using [the manifest](slack-app-manifest.yaml),
enables Socket Mode, and provisions an app-level token with `connections:write`.
Register one **HTTPS** redirect URL ending in `/chat/authorize/slack` in the app's
OAuth settings. Set the same exact address in Forge. For example,
`https://localhost:5182/chat/authorize/slack` works only after an HTTPS listener
and a locally trusted certificate are configured on that address. Forge's existing
HTTP port 5180 is not an HTTPS callback. An existing HTTPS reverse proxy can also
forward this path to the same Forge process.

The host needs the Slack Client ID, App ID, a protected client-secret reference,
and the shared app-token reference. Users then select a workspace and approve
the requested bot scopes. Forge retrieves `access_token`, `team.id`, `team.name`
and `app_id` from `oauth.v2.access`; users do not copy these into the connection form.

Public channels require `chat:write`, `channels:read`, and `channels:history`.
Selecting private channels adds `groups:read` and `groups:history`; the shared app
must also subscribe to `message.groups`. Keep token rotation disabled for this
adapter. Enterprise-wide installations, rotated/refresh tokens, another App ID,
or missing scopes are rejected rather than saving an unusable connection.

Slack OAuth does **not** return the `xapp` Socket Mode token. It remains a shared
host credential and every connection created by this flow uses the same reference.
The app must be allowed for the target workspace; cross-workspace distribution and
workspace approval policies are managed in Slack. Invite the installed app to the
channel after authorization.

Sources: [Slack OAuth](https://docs.slack.dev/authentication/installing-with-oauth/),
[OAuth exchange](https://docs.slack.dev/reference/methods/oauth.v2.access/),
[Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/).

### Telegram

For the first connection, open **Settings → Chat → Add Telegram connection** (or
**Set up connection** in the row drawer). The **Enable Telegram connections** panel
replaces the disabled Connect button:

1. Select **Open BotFather**. Create a dedicated bot using BotFather's Mini App or
   `/newbot`, with a unique username ending in `bot`.
2. Open [BotFather's Mini App](https://t.me/Botfather?startapp=). In Telegram Desktop,
   click the blue **Open App** button in BotFather's right-hand profile panel, or
   **Open** beside the message box. Select the new manager bot from step 1, scroll
   below the token's **Copy / Revoke** buttons to **Settings → Bot Settings**, and
   enable **Bot Management Mode**. `/setprivacy` controls group
   message visibility and does not enable bot management. If the app cannot open,
   update Telegram and try again.
3. Copy its API token from BotFather into **Manager bot token**, then select
   **Verify and continue**. Forge checks the bot and manager capability, saves the
   credential encrypted, and opens Telegram's native connection approval prompt.

This initial creation/credential handoff cannot be replaced by the managed-bot
approval link: Telegram requires a manager bot with its management permission
already enabled. Forge has no shared public manager service. The first token is
entered once per host; subsequent connections use native approval without token
or bot-ID entry. No host JSON edits or extra restart are needed after registration.
The form clears the token when submitted, never saves it in browser storage, and
gives a specific retry instruction if validation fails.

The manager must have no webhook and must not be a delivery bot, a legacy bridge
bot, or a manager consumed by another application. Forge checks locally registered
delivery/legacy bots and Telegram's webhook status before saving or polling. It
cannot discover another application's long-poll consumer, so use a dedicated bot.
It never deletes a webhook or rotates an existing bot token during authorization.

The button opens Telegram's native bot-creation confirmation:
`https://t.me/newbot/<manager>/<generated_username>?name=Forge`.
The user signs in if needed and approves creating a managed bot. Keep the suggested
username so Forge can match the approval; the display name can be changed. Each
suggested username contains 112 random bits and meets Telegram's 32-character limit.
If the user edits the username, the unrelated result is ignored; start again with
the generated username. The previously created bot remains in Telegram.

Forge serializes polling for this dedicated manager, requests only `managed_bot`
updates, matches the exact generated username to a pending unexpired request,
retrieves the new token with `getManagedBotToken`, and verifies it with `getMe`.
Other bot creations or token changes never attach to a pending connection. No chat
message is sent by this setup flow. Add the new bot to the desired group/channel
after saving. Telegram account login alone does not grant access to an existing bot.

Sources: [Managed bots](https://core.telegram.org/bots/features#managed-bots),
[Native creation links](https://core.telegram.org/api/bots/managed-bots),
[Bot API token retrieval](https://core.telegram.org/bots/api#getmanagedbottoken).

## Host configuration

Telegram's in-app first-manager setup is the default. It writes to the existing
Windows DPAPI store at the stable reference `dpapi/chat-authorization-telegram-manager`,
under the Forge state folder, and reads that slot on each authorization request.
Fresh runtime instances read the same encrypted credential. Repeated same-token
registration is idempotent; a different existing manager is never overwritten.
An explicitly configured `Telegram.ManagerToken` reference takes precedence and
cannot be changed by this first-time setup endpoint.

For an existing shared Slack OAuth app or externally managed Telegram registration, bind the following
settings through the host's normal `Forge:Chat` configuration.
These are **references**, never raw credentials in checked-in JSON. Use the existing
`ChatDpapiSecretStore` under `<Forge state folder>/chat-secrets` or an installed
external `IChatSecretStore` to provision the referenced secrets. The built-in
`environment` store intentionally accepts only `FORGE_CHAT_TEST_*` keys and is for
isolated test profiles; do not broaden it to arbitrary environment variables.

```json
{
  "Forge": {
    "Chat": {
      "Authorization": {
        "Slack": {
          "ClientId": "123456.789012",
          "AppId": "A0123456789",
          "RedirectUri": "https://localhost:5182/chat/authorize/slack",
          "ClientSecret": { "Store": "dpapi", "Key": "slack-oauth-client" },
          "AppToken": { "Store": "dpapi", "Key": "slack-shared-app" }
        },
        "Telegram": {
          "ManagerToken": { "Store": "dpapi", "Key": "telegram-manager" }
        }
      }
    }
  }
}
```

An HTTPS API listener must also be included in `Forge:Identity:ApiOrigins`.
For a reverse proxy, preserve the existing exact host/origin policy; do not expose
the API directly on all interfaces. The Vite proxy forwards `/chat/authorize` to the
API. Certificates, listeners and Slack registration are separate host setup.
The September 11 Telegram manager registration is recorded below. Changes to
startup configuration require an explicitly approved service restart; saving
the Telegram manager through the new API applies immediately.

## API and security behavior

| Request | Result |
|---|---|
| `GET /api/chat/authorization` | Provider readiness without credential values |
| `POST /api/chat/authorization/slack/setup` | Host-admin start with `{providerId: "slack", includePrivateChannels}`; returns the short-lived approval request |
| `POST /api/chat/authorization/{id}/slack/approve` | Protected `{code}` exchange followed by automatic app creation/install; returns only review metadata |
| `POST /api/chat/authorization/telegram/manager` | Host-admin first-time setup with `{token}`; verifies and stores encrypted, returns only `{username}` |
| `POST /api/chat/authorization` | Start with `{providerId, includePrivateChannels}` |
| `GET /chat/authorize/slack?state=…&code=…` | Anonymous, state-bound callback; records a single code only |
| `POST /api/chat/authorization/{id}/poll` | Authenticated code exchange / managed-bot approval check |
| `POST /api/chat/authorization/{id}/save` | `{displayName, productIds}` creates one enabled connection |
| `DELETE /api/chat/authorization/{id}` | Cancel pending setup and discard credentials |

Every API operation requires a currently authorized host administrator. Mutations,
including polling, require the paired browser's CSRF proof and trusted origin.
Manager setup rechecks the administrator after provider validation, uses atomic
DPAPI persistence, and serializes duplicate registration. Its request body and
manual connection-credential bodies are redacted in audit records; raw bodies are
not buffered for auditing. Registration sends no chat messages and creates no
connection, grants, routes or engine cutover by itself.
Flows are bound to the operator ID and enrollment revision, expire after ten
minutes, and are limited to four active requests per operator and 64 total per host.
Restart invalidates unfinished flows; an idle timer removes expired credentials.
Cancellation stops Forge setup but does not uninstall an app or delete a provider bot.

Only the callback is anonymous. It cannot create a connection, link a Forge identity,
or change permissions. Duplicate/unknown/expired state is rejected. OAuth exchange
is attempted once, including after uncertain transport failure. A verified result is
reviewed before saving. Save is idempotent for a lost response and rejects an existing
installation rather than overwriting it. Credential persistence uses a fresh DPAPI
reference and the existing connection/product-grant transaction. Failed writes clean
up only their own blob after checking that a committed row does not reference it.

Provider responses have a size limit and timeout; errors never return raw response
bodies, tokens or token-bearing Telegram URLs. The callback has `no-store`, a restrictive
CSP, and `no-referrer`. Hosting request-start/finish diagnostics are filtered because
they include callback query strings. Any reverse proxy or tracing layer must also
avoid recording authorization queries. The UI uses protected provider windows with
no opener and supports blocked popups through an explicit link. Provider isolation
can prevent automatic window closure; completion does not depend on window handles.

## Validation and activation

- [x] Automatic Slack setup: 457 backend regression cases pass, one live-provider
      case skipped. This includes 14 new provisioning cases and all 35 existing
      authorization cases. UI build and all 30 isolated browser cases pass.
      The popup works in Settings and the row drawer; Escape closes only the
      popup and preserves the row's draft. Evidence:
      `.tmp-build/slack-connect/{slack-regression.trx,backend-regression.log,browser-full.log,ui-build.log}`
      and `slack-approval-narrow.png`.
- [x] Deploy the Slack provisioning endpoints after explicit user approval
      (September 12, 04:02 UTC; September 11 local time). API PID 70188 → 21464;
      UI PID 72132 unchanged. Health/access checks pass; the live button is enabled
      and the old registration notice is gone. Telegram connections and identity
      remain intact. Live approval-ticket creation returned HTTP 400, so this does
      not close the popup/consent gate. Evidence: `.tmp-build/slack-connect/deploy-first-after.json`
      and `deployed-smoke-first.json`.
- [x] Diagnose the live failure: Slack requires CLI identification in User-Agent.
      Without it, `generateAuthTicket` returned `internal_error`; a direct request
      with `slack-cli/v4.7.0 (os: windows) Forge/1.0` returned a ticket. Add the
      header to provisioning calls, identify Forge explicitly and map internal
      provider failures to unavailable. Evidence: `.tmp-build/slack-connect/protocol-probe.json`.
      All 50 focused authorization cases pass after correction, including 15 Slack
      setup cases. Required-header and internal-error behavior are covered in
      `.tmp-build/slack-connect/slack-protocol-fix.trx` and `backend-protocol-fix.log`.
- [x] Deploy the header correction after the user's second explicit restart
      approval (September 12, 05:29 UTC). API PID 21464 → 54028; UI PID 72132
      retained. Live setup returns 200 with a real Slack approval request and
      opens the modal. Close/reopen, cancellation, narrow scrolling and visible
      controls pass; no page/API errors or saved-connection changes occur. The
      temporary verification session is logged out. Evidence:
      `.tmp-build/slack-connect/{deploy-after,deployed-smoke}.json`,
      `deploy-protocol-fix.log` and `deployed-slack-approval{,-narrow}.png`.
- [x] Correct the blank external window reported after deployment. Slack now opens
      directly during the click; clipboard permission cannot delay navigation.
      A bounded fallback handles rejected or stalled copying without claiming
      success. The regression failed with `about:blank` before the fix; all 31
      browser cases and UI build pass afterward. The running Vite UI serves the
      fix without a restart. Live verification uses a real ticket and intercepts
      the external Slack page; it confirms navigation, fallback and cancellation,
      with no connection changes. Evidence: `.tmp-build/slack-connect/clipboard-before.log`,
      `browser-clipboard-full.log`, `ui-build-clipboard.log` and `deployed-clipboard.json`.
- [x] Add workspace signup/discovery and a return prompt that renews approval,
      including after the old request expires. Preserve private-channel choices,
      clear the old code and show renewal failures inside the modal for retry.
      All 33 browser cases and UI build pass. Live HMR verification uses an
      intercepted signup page and real ticket creation/renewal (both 200), with
      reachable narrow controls, cancelled test flows and unchanged connections.
      No service restart or real workspace creation occurs. Evidence:
      `.tmp-build/slack-connect/{browser-workspace-full,ui-build-workspace}.log`,
      `deployed-workspace.json` and `deployed-workspace-{choice,signup-narrow}.png`.
- [x] Verify the user's `forge` workspace and automate the request handoff in its
      existing Slack window (September 12, 07:18 UTC). A fresh request submitted to
      `#all-forge` opens the real **Slack CLI Authentication** permission dialog.
      The earlier rejection's cause is unproven; fresh submission succeeds.
      Evidence: `.tmp-build/slack-connect/slack-real-permission-dialog.png` and
      `live-handoff.json`. This agent-assisted action does not change the manual
      handoff in the browser-only setup screen.
- [x] Complete explicitly approved Slack consent, capture/exchange the challenge
      automatically, install the app and save its credentials through protected
      Forge endpoints. Add the app to `#all-forge` and confirm discovery membership.
      See [live evidence](chat-slack-live-setup.md), including the earlier failed
      approval endpoint and the exact successful assisted path.
- [x] Correct the confirmed conversation request encoding problem; 69 Slack tests pass.
- [ ] Deploy after fresh restart approval, revalidate the saved channel, then perform
      the separate real-provider exercise and personal Slack identity proof.
- [x] Telegram identity follow-up: numeric sender validation, read-only installation,
      unambiguous verified-ID autofill, direct confirmation popup with a blocked-popup
      fallback, automatic link refresh, stale/expired challenge cleanup and no raw
      Telegram command. Slack autofill stays inside its workspace. UI build and all
      27 browser cases pass. Live Settings verifies the linked account and updated
      controls without chat mutations or service restarts. Evidence:
      `.tmp-build/chat-manager/identity-{browser-final,ui-build}.log` and
      `identity-ui-live.{json,png}` in the same directory.
- [ ] First-time personal ID discovery inside Forge. The form still requires a
      numeric ID when no verified identity can be reused. The live account below
      was linked with Desktop assistance and actual provider proof.
- [x] First-manager follow-up: 17 backend cases cover registration, fresh-state
      persistence, current role/CSRF/origin, revocation during provider checks,
      invalid credentials, delivery/webhook conflicts, duplicate registrations,
      external-reference precedence, storage failure and audit redaction.
      Final authorization run: 35 passed. Broader regression before the final
      invalid-token feedback refinement: 442 passed, one live test skipped.
- [x] First-manager UI build, 197 unit tests and all 22 browser cases pass. New browser
      cases exercise registration → consent → save, skipping registration after
      reload, failure/retry, popup blocking and row-drawer cancellation.
      Isolated desktop/narrow screenshots show reachable controls and no horizontal
      overflow. Evidence: `.tmp-build/chat-manager/` and
      `.tmp-build/chat-manager-{regression,focused,browser-full,ui-build,ui-unit}.log`.
- [x] Deploy the first-manager setup API after a new approved restart (September 11).
      API PID 28340 → 70188, UI PID 72132 retained. Direct/proxied health returns 200;
      anonymous readiness/setup returns 401. Authenticated readiness now exposes
      `canConfigure: true`, and live Settings renders **Enable Telegram connections**,
      **Open BotFather**, the protected token field and **Verify and continue**.
      The old disabled Telegram Connect button is absent. No API/page errors or
      configuration mutations occurred, and the temporary test session was logged out.
      Evidence: `.tmp-build/chat-manager/deployed-smoke.json`, deployment logs and
      `deployed-telegram-manager-*.png` in the same directory.
      At 390px, Settings and the setup panel fit and Close remains reachable. The
      global topbar has existing overflow (document width 847px both before and
      after opening setup); this is separate from the Settings scroll container.

The items below record the earlier consent feature deployment.

- [x] Backend builds in isolated artifacts without stopping the running host.
- [x] Full focused backend regression passes: 426 passed, one explicit live-provider
      test skipped. Results: `.tmp-build/chat-authorization/authorization-backend.trx`.
- [x] Eighteen authorization tests cover consent, encrypted save/idempotency, grants,
      role/CSRF/ownership, revoked roles, expiry/cancel/replay, unsupported Slack
      installation/scopes/rotation, redacted malformed/oversized/failed responses,
      missing registration/HTTPS, Telegram matching and manager isolation.
- [x] Twenty browser cases pass, including both consent/save flows, denied consent,
      blocked popups, retry/cancel, missing registration and all prior Settings/drawer
      regression cases. Provider pages/API responses are isolated fixtures.
- [x] UI build and all 197 UI unit tests pass.
- [x] Deploy the new backend after per-restart approval (2026-09-11). API PID
      changed from 81444 to 28340; UI PID 72132 stayed running. Direct/proxied health
      returned 200, paired authorization readiness returned 200, anonymous access
      returned 401, and an invalid callback was rejected with 400 and `no-store`.
- [x] Check both provider screens on the deployed Settings page. Connect/Close are
      visible, missing registration is explained, and no console/API errors or
      configuration mutations occurred. The temporary verification session was logged out.
- [x] Verify provider selection persists after focus moves, switches between providers,
      and clears on Close; the disabled button explains the missing host registration.
      Five focused browser cases and UI build pass. Live evidence:
      `.tmp-build/chat-authorization/provider-selection-smoke.json` and
      `selected-{telegram,slack}-authorization.png` in the same directory.
- [ ] Complete a real Slack installation through automatic provisioning. An HTTPS
      callback is needed only if choosing the existing shared OAuth app path.
- [x] Complete the actual Telegram manager registration (September 11, 21:25 UTC).
      With the user-created dedicated bot selected, targeted Desktop automation
      enabled **Bot Settings → Bot Management Mode** without moving the cursor or
      changing the foreground window. The token was copied directly into Forge's
      protected setup request, stored encrypted, and cleared from the clipboard.
      Live verification at 21:27 UTC reports Telegram `available: true`, an enabled
      **Connect with Telegram** button and no first-manager form or page errors.
      API PID 70188 still serves port 5180; no restart was needed. At that initial
      registration check the engine was `legacy`, with zero plugin connections. Evidence:
      `.tmp-build/chat-manager/telegram-manager-live-verified.json`,
      `deployed-telegram-connect-ready.png` and `telegram-manager-mode-enabled.png`.
- [x] User completed real Telegram consent/save after manager setup. Authenticated
      live checks confirm one enabled connection with all five selected product
      grants; destination selection and identity proof were separate at that point.
- [x] After explicit user approval, import and cut over the live host to `plugins`
      (22:06 UTC, revision 2) without restarting API PID 70188. Preserve historical
      notifications and Host alerts; leave unverified legacy scopes disabled.
- [x] Verify the personal Telegram account through a provider-received challenge
      on the new bot (22:10 UTC). Live API and Settings confirm the link. The new
      receiver is ready and management is interactive. See the
      [activation review](chat-plugin-activation-review.md) for evidence and scope.
- [ ] Complete provider channel checks; real Slack consent/save is verified above. The new Telegram
      connection still has no destination. Fixture success and a personal identity
      link are not proof of channel delivery or remote actions.

Evidence: `.tmp-build/chat-authorization-tests.log`,
`.tmp-build/chat-authorization-browser-final.log`,
`.tmp-build/chat-authorization-ui-build.log`,
`.tmp-build/chat-authorization-ui-unit.log`.

Deployment evidence: `.tmp-build/chat-authorization/deploy-before.json`,
`deploy-after.json`, `deploy.log`, `deployed-smoke.json`, and
`deployed-{slack,telegram}-authorization.png` in that directory. The loaded source
is `0d790898` plus the uncommitted authorization changes. Following the separate
Telegram registration above, Telegram is available; Slack still reports
`authorization-registration-required`. After the approved activation the active
engine is `plugins`, with the user's new Telegram connection and the imported legacy
connection enabled. Personal identity is verified for the new bot. That connection
still needs a destination; existing legacy Host alerts and the verified local parent
route were retained. Connection authorization, identity proof and channel delivery
remain independently verified steps.