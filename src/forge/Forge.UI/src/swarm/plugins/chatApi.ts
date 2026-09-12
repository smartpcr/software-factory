import { operatorFetch } from '@/api/operatorSession';

export interface ChatSummary {
  state: string; revision: number; destinations: { destinationId: string; connectionId: string; providerId: string; displayName: string }[];
  unroutedCount: number; unroutedReasons: Record<string, number>; pendingCount: number; failedCount: number; sentCount: number;
}
export interface ChatSummaryItem { spawnId: string; title: string; summary: ChatSummary }
export interface ChatConnection { id: string; providerId: string; displayName: string; enabled: boolean; revision: number; state: string; reason: string | null; installationId?: string }
export interface ChatConnectionDetails extends ChatConnection {
  installationId: string; settingsVersion: number; settings: Record<string, unknown>;
  secretReferences: Record<string, { store: string; key: string }>; productIds: string[];
}
export interface ChatDestination { id: string; displayName: string; connectionId: string; kind: string; channelId: string; externalTenantId: string; revision: number; accessState: string }
export interface ChatSubscription { id: string; revision: number; destinationId: string; conversationPolicy: string; interactive: boolean; contentMode: string; paused: boolean; eventKindsJson: string }
export interface ChatPlugin { productId: string; spawnId: string; enabled: boolean; revision: number; reason: string | null; subscriptions: ChatSubscription[] }
export interface ChatSetup { plugin: ChatPlugin; canManage: boolean; canManageConnections: boolean; interactiveAvailable: boolean; redactedContentAllowed: boolean; hostReason: string | null; notificationOnlySlice?: boolean }
export interface ChatRoute { destinationId: string; conversationPolicy: string; interactive: boolean; contentMode: string; eventKinds?: string[]; paused?: boolean }
export interface ChatDraft { expectedRevision: number; enabled: boolean; destinations: ChatRoute[]; activateNewRun: boolean }
export interface ChatManagement {
  slackLinkConfirmationAvailable?: boolean;
  recentLinkTargets?: { connectionId: string; externalTenantId: string; externalUserId: string }[];
  canManageConnections: boolean; canManageHostAlerts: boolean; hostReason: string | null; interactiveAvailable: boolean; hostRedactedContentAllowed: boolean;
  products: { id: string; name: string }[]; links: { providerId: string; externalTenantId: string; externalUserId: string }[];
  secretStores: { id: string; available: boolean; externallyManaged: boolean }[];
}
export interface ChatHealth {
  connectionId: string; revision: number; appliedRevision: number; state: string; reason: string | null; lastSendAt: number | null; lastReceiveAt: number | null;
  credentials: { id: string; configured: boolean; store: string | null; externallyManaged: boolean; available: boolean }[];
}
export interface ChatCheck { id: string; state: string; reason: string | null; createdAt: string; sentAt: string | null; replyAt: string | null; connectionRevision: number; destinationRevision: number }
export interface ChatHistory { unrouted: Record<string, number>; deliveries: { id: string; connectionId: string; state: string; attempts: number; lastError: string | null; nextAttemptAt: number }[] }
export interface ChatHostStatus { chat: ChatHistory; orphaned: number; degraded: number }
export interface ChatProvider { id: string; displayName: string; capabilities: { topics: boolean; threads: boolean; destinationDiscovery: boolean } }
export interface ChatDiscovery { items: { address: { externalTenantId: string; channelId: string }; displayName: string; kind: string }[]; nextCursor: string | null }
export interface ChatLinkChallenge { token?: string; expiresInSeconds: number; deepLink: string | null; channelCommand?: string | null; channelName?: string }

const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';
const enc = encodeURIComponent;
export const rowPath = (productId: string, spawnId: string) => `/api/products/${enc(productId)}/story-spawns/${enc(spawnId)}/plugins/chat`;
export const connectionPath = (id: string) => `/api/chat/connections/${enc(id)}`;
export async function chatRequest<T>(path: string, method = 'GET', body?: unknown, productId?: string): Promise<T> {
  const response = await operatorFetch(baseUrl + path, {
    method, cache: 'no-store', headers: { ...(productId ? { 'X-Forge-Product-Id': productId } : {}), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string } | null;
    throw new Error(response.status === 409 && problem?.title === 'revision-conflict'
      ? 'Settings changed in another window. Reload the saved settings before trying again.'
      : response.status === 403 ? 'Your operator account does not have permission for this change.'
      : response.status === 401 ? 'Pair this browser with your Forge operator account in Settings.' : chatReason(problem?.title ?? 'request-failed'));
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
export async function providerRequest<T>(path: string, method = 'GET'): Promise<T> {
  const result = await chatRequest<{ succeeded: boolean; value: T; failure: { code: string } | null }>(path, method);
  if (!result.succeeded) throw new Error(chatReason(result.failure?.code ?? null));
  return result.value;
}
export const chatApi = {
  get: (p: string, s: string) => chatRequest<ChatSetup>(rowPath(p, s) + '/setup', 'GET', undefined, p),
  save: (p: string, s: string, draft: ChatDraft) => chatRequest<ChatSetup>(rowPath(p, s) + '/setup', 'PUT', draft, p),
  summaries: (p: string) => chatRequest<ChatSummaryItem[]>(`/api/products/${enc(p)}/chat/summaries`, 'GET', undefined, p),
  connections: (p?: string) => chatRequest<ChatConnection[]>('/api/chat/connections' + (p ? `?productId=${enc(p)}` : '')),
  destinations: (p: string | undefined, c: string) => chatRequest<ChatDestination[]>(connectionPath(c) + '/destinations' + (p ? `?productId=${enc(p)}` : '')),
  management: () => chatRequest<ChatManagement>('/api/chat/management'),
  providers: () => chatRequest<{ providers: ChatProvider[] }>('/api/chat/providers'),
  details: (c: string) => chatRequest<ChatConnectionDetails>(connectionPath(c)),
  health: (c: string) => chatRequest<ChatHealth>(connectionPath(c) + '/health'),
  discover: (c: string, cursor?: string) => providerRequest<ChatDiscovery>(connectionPath(c) + '/discovery' + (cursor ? `?cursor=${enc(cursor)}` : '')),
  validate: (c: string, d: string) => providerRequest<unknown>(connectionPath(c) + `/destinations/${enc(d)}/validate`, 'POST'),
};

export function chatReason(code: string | null): string {
  const reasons: Record<string, string> = {
    'authorization-registration-required': 'Provider authorization has not been configured on this Forge host.',
    'authorization-https-required': 'Slack needs a registered HTTPS return address ending in /chat/authorize/slack.',
    'authorization-expired': 'This authorization request expired or belongs to another session. Start authorization again.',
    'authorization-denied': 'Authorization was declined or cancelled. You can try again when ready.',
    'authorization-manager-token-invalid': 'Paste the full API token from BotFather, including the number, colon and secret.',
    'authorization-slack-code-required': 'Paste the confirmation code from Slack’s permission dialog.',
    'authorization-slack-externally-configured': 'This host uses an existing Slack app registration. Use its Connect with Slack flow.',
    'authorization-slack-admin-approval-required': 'Your Slack workspace requires an administrator to approve the new app. Open the app in Slack to review its approval status. Forge has not sent an approval request or saved this connection.',
    'authorization-telegram-token-rejected': 'Telegram rejected this bot token. Copy its current API token from BotFather and try again.',
    'authorization-manager-externally-configured': 'This host already uses an externally configured Telegram manager. Reload Settings to use it.',
    'authorization-manager-already-configured': 'Another Telegram manager is already registered on this host. Close and reopen this setup to use it.',
    'authorization-manager-save-failed': 'Forge could not save the manager credential. Check that protected storage on this host is writable, then paste the token and retry.',
    'authorization-limit': 'Too many authorization windows are open. Close an unfinished setup or wait ten minutes, then try again.',
    'authorization-not-approved': 'Approve the connection in the provider window before saving.',
    'authorization-provider-unavailable': 'The provider could not complete authorization. Check your connection and start again.',
    'authorization-provider-rejected': 'The provider rejected authorization. Check the host app registration and start again.',
    'authorization-installation-unsupported': 'Slack returned an unsupported installation. Use the registered app with the requested bot scopes, a single workspace, and token rotation disabled.',
    'authorization-manager-required': 'The dedicated Telegram manager needs Bot Management Mode enabled in BotFather.',
    'authorization-manager-dedicated': 'The Telegram manager is already used for chat delivery. Register a separate manager bot.',
    'authorization-manager-webhook': 'The Telegram manager has a webhook. Use a dedicated manager without a webhook for Forge authorization.',
    'credentials-required': 'This saved connection needs credentials. Use Repair / edit to finish setup.',
    'slack-membership-required': 'Invite the Slack app to this channel, then validate again.',
    'slack-link-user-not-in-channel': 'Join the selected Slack channel with your account, then send the confirmation again.',
    'invalid-link-target': 'Enter your personal Slack member ID (starting with U or W), not a name or bot ID.',
    'slack-send-outcome-unknown': 'Slack did not confirm the request. Keep Slack open, then request a fresh confirmation.',
    'slack-channel-archived': 'This Slack channel is archived. Unarchive it or choose another channel.',
    'slack-bot-token-revoked': 'Replace the revoked Slack credential, save the connection, then validate again.',
    'slack-app-token-conflict': 'Connections for one Slack app must share the same app credential. Disable the affected connections, update their references together, then enable them.',
    'slack-workspace-mismatch': 'The bot token belongs to a different workspace. Check the workspace ID and credential.',
    'slack-app-mismatch': 'The socket credential belongs to a different app. Check the App ID and app token.',
    'slack-app-token-revoked': 'Replace the revoked Slack app token. Connections sharing this app must use the same credential; disable those connections during rotation, then enable them together.',
    'slack-app-scope-required': 'The Slack app token needs connections:write. Replace it with a token that has that scope.',
    'slack-channel-unavailable': 'Slack cannot access this channel. Check the workspace, channel ID and membership, or choose another channel.',
    'slack-private-channels-disabled': 'Enable private channel discovery on this connection and grant the app access to the private channel.',
    'telegram-token-revoked': 'Replace the Telegram bot token and save the connection, then validate again.',
    'telegram-destination-access-required': 'Check the Telegram bot membership and channel permissions, then validate again.',
    'slack-invalid-auth': 'Slack rejected the credential. Replace it and save the connection.',
    'slack-bot-scope-required': 'The Slack installation is missing a required scope. Update the app installation, then validate again.',
    'slack-rate-limited': 'Slack asked Forge to wait. Delivery will use the next retry time shown below.',
    'session-unavailable': 'The connection is not ready yet. Save the credentials and enabled connection, then retry validation.',
    'connection-unavailable': 'Enable the saved connection before validating its channels.',
    'credential-store-unavailable': 'Protected credential storage is unavailable on this host. Use an externally managed reference.',
    'invalid-provider-settings': 'Check the provider settings and installation identifiers.',
    'invalid-destinations': 'Choose at least one channel and remove duplicate destinations.',
    'configuration-conflict': 'This installation or destination already exists. Reload and select the saved connection.',
    'legacy-engine-active': 'This host is using the legacy chat engine. A host administrator must select the plugin engine and refresh the host before these routes can deliver.',
    'host-chat-disabled': 'Chat is disabled on this host.',
    'telegram-slice-disabled': 'Telegram row notifications are not enabled on this test profile.',
    'new-test-bot-required': 'A host administrator must confirm a new test bot for this profile.',
    'isolated-profile-required': 'A host administrator must configure an isolated test profile.',
    'empty-legacy-profile-required': 'This profile contains legacy chat data. Use a fresh test profile.',
    'slice-requires-one-route': 'This test profile supports one story and one Telegram connection. Turn off the other story first.',
    'new-run-needs-activation': 'This is a new run. Enable notifications to create a fresh route.',
    'setup-pending': 'Settings saved. The introduction is queued for Telegram.',
    'route-needs-setup': 'The saved route is no longer available. Choose a channel and save again.',
    'destination-required': 'Choose a channel before enabling notifications.',
    'telegram-connection-required': 'Choose an enabled Telegram connection.',
    'test-bot-token-required': 'The host needs the test bot credential for this connection.',
    'test-bot-identity-unverified': 'The test bot identity could not be verified. Ask the host administrator to check the connection.',
    'telegram-destination-unverified': 'Telegram could not verify access to this channel. Check the bot membership and saved channel.',
    'telegram-send-unconfirmed': 'Delivery is unconfirmed. Check Telegram before saving a fresh setup; Forge will not automatically resend.',
    'provider-unavailable': 'This provider is unavailable on the host.',
    'product-db-unavailable': 'This product is temporarily unavailable. Chat delivery is paused until it is available again.',
  };
  return code ? reasons[code] ?? 'Chat needs attention. Check the connection and try saving again.' : '';
}
