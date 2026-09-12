# Slack live setup and Settings layout — September 12, 2026

## Verified live setup

- [x] After the user's explicit approval of Slack's developer terms and app-management
  permissions, submit a fresh request in the existing Slack window, confirm it and
  capture/exchange the resulting challenge automatically (07:42 UTC). The user does
  not have to copy the code. Tooling credentials remain in memory only.
- [x] Verify workspace `T0C1ATNTPNJ` (`forge`) and the approving account with `auth.test`.
- [x] Create and install app `A0C16PQFQD9`; verify bot `B0C27BRE20G`, its workspace,
  and the Socket Mode endpoint (07:48 UTC).
- [x] Save and enable connection `slack-t0c1atntpnj`, revision 4, with the
  `code-intelligence` product grant. Both bot/app credentials use the protected DPAPI
  store. Live connection health initially reports `ready`.
- [x] Add the installed Forge app to `#all-forge` through Slack's channel app picker.
  Discovery confirms membership in `C0C187JN3T7`. Save destination
  `slack-t0c1atntpnj-all-forge` (07:53 UTC).
- [x] Diagnose the subsequent channel-validation failure. The live JSON POST to
  `conversations.info` returns `invalid_arguments` / `missing required field: channel`;
  the same request with form parameters, and with GET query parameters, succeeds
  and confirms membership. The destination was `unavailable` at that checkpoint;
  the approved deployment and revalidation below resolve it.
- [x] Change conversation info/list requests to form encoding. Strengthen the shared
  fixture's transport checks and cursor/filter assertions. All 69 Slack setup,
  protocol and runtime tests pass using isolated build artifacts.
- [x] Restart Forge.Api after explicit user approval (08:55 UTC): API PID 54028 →
  39092, UI PID 72132 retained. `/api/health` directly and through Vite returns
  HTTP 200 / `ok`. The clean build includes the Slack transport correction and
  section 5's provider cancellation guards.
- [x] Discover and validate the saved `#all-forge` channel through Forge at
  08:56 UTC, then through the actual Settings UI at 09:00 UTC. Destination access
  is `ready` at routing revision 1; connection health is `ready` at saved/applied
  revision 4/4. One Slack and two Telegram receivers recover to `receiving`.
- [x] Verify the deployed Settings layout at 1440px/390px and select/cancel the
  Slack host-alert draft. No page errors or horizontal overflow; connections,
  operator links and saved host-alert routes are unchanged. Evidence is in
  `.tmp-build/chat-release/{deploy-before,deploy-after,deployed-settings}.json`
  and `settings-verified-{1440,390}.png`.
- [ ] Verify Slack operator identity and the separate real-provider delivery/reply
  exercise, including cross-provider actions. No explicit test message was sent.

The earlier Forge approval attempt returned `authorization-installation-unsupported`.
Its cause remains unproven. The successful assisted setup used the official Slack
CLI exchange/create/install protocol directly and saved credentials through Forge's
protected configuration endpoints. This establishes a real installation, but does
not claim that the browser-only copy/paste flow has become automatic or that its
entire authorization endpoint was verified by this run.

Evidence under `.tmp-build/slack-connect/`: `automatic-connection.json` (earlier
failure), `automatic-connection-probe.json` (successful consent/install/save),
`saved-connection-validation.json`, `channel-transport.json`,
`slack-channel-transport.trx`, and `backend-channel.log`. Temporary local operator
sessions are logged out. No plaintext provider credentials are written to these
files. Existing Telegram connections and subscriptions are preserved.

## Settings layout

- [x] Separate a consistent saved-connection list from provider setup and selected
  connection channels. Highlight selection and align edit actions.
- [x] Put account linking and host alerts in secondary panels. Collapse verified
  account records and host configuration while keeping status visible.
- [x] Use the existing Forge palette with readable body typography, provider marks,
  bounded long names, and a layout that stacks at narrow container widths.
- [x] Preserve channel errors during refresh, draft state across Settings tabs,
  provider setup/Close actions, and a single page scrollbar.
- [x] UI build and all 33 chat browser cases pass. The running `:5181` page is verified
  at 1440px and 390px: zero horizontal overflow, visible Close, selection/setup
  controls working, no page errors, and unchanged saved connection configuration.
  This frontend update is live through Vite HMR; no service restart was performed.

Evidence: `.tmp-build/slack-connect/{browser-layout.log,ui-build-layout.log,deployed-layout.json}`
and `deployed-layout-{1440,390,selected,setup-narrow}.png`. API PID 54028 and Vite
PID 72132 remained running at the layout checkpoint. The later approved backend
deployment is recorded above; API PID 39092 served the verified channel transport
correction. The subsequent approved 09:41 UTC deployment runs API PID 43620 with
the [private identity-confirmation flow](chat-plugin-release-gate.md#slack-identity-confirmation-follow-up-2026-09-12).
Personal Slack approval and saving a future host-alert destination remain separate
setup steps.