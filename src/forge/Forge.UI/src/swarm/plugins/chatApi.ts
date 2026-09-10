import { operatorFetch } from '@/api/operatorSession';

export interface ChatSetup {
  enabled: boolean;
  revision: number;
  state: 'off' | 'needs-setup' | 'ready';
  reason: string | null;
  destinationId: string | null;
  connectionId: string | null;
  canManage: boolean;
  lastSentAt: string | null;
  preview: string;
}
export interface ChatConnection { id: string; providerId: string; displayName: string; enabled: boolean; state: string; reason: string | null }
export interface ChatDestination { id: string; displayName: string; connectionId: string; kind: string }
export interface ChatDraft { expectedRevision: number; enabled: boolean; destinationId: string | null; activateNewRun: boolean }

const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';
const rowPath = (productId: string, spawnId: string) =>
  `/api/products/${encodeURIComponent(productId)}/story-spawns/${encodeURIComponent(spawnId)}/plugins/chat/notification-setup`;

async function request<T>(productId: string, path: string, body?: ChatDraft): Promise<T> {
  const response = await operatorFetch(baseUrl + path, {
    method: body ? 'PUT' : 'GET',
    headers: { 'X-Forge-Product-Id': productId, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => null) as { title?: string } | null;
    throw new Error(response.status === 409 && problem?.title === 'revision-conflict'
      ? 'Chat settings changed in another window. Close and reopen Chat to load the saved version.'
      : response.status === 403 ? 'A product owner must manage notifications for this story.'
      : chatReason(problem?.title ?? 'request-failed'));
  }
  return response.json() as Promise<T>;
}

export const chatApi = {
  get: (productId: string, spawnId: string) => request<ChatSetup>(productId, rowPath(productId, spawnId)),
  save: (productId: string, spawnId: string, draft: ChatDraft) => request<ChatSetup>(productId, rowPath(productId, spawnId), draft),
  connections: (productId: string) => request<ChatConnection[]>(productId, `/api/chat/connections?productId=${encodeURIComponent(productId)}`),
  destinations: (productId: string, connectionId: string) => request<ChatDestination[]>(productId,
    `/api/chat/connections/${encodeURIComponent(connectionId)}/destinations?productId=${encodeURIComponent(productId)}`),
};

export function chatReason(code: string | null): string {
  const reasons: Record<string, string> = {
    'legacy-engine-active': 'This host uses legacy chat. A separate test profile is required for Telegram row notifications.',
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
    'provider-unavailable': 'The Telegram provider is unavailable on this host.',
    'product-db-unavailable': 'This product is temporarily unavailable. Chat delivery is paused until it is available again.',
  };
  return code ? reasons[code] ?? 'Chat needs attention. Check the connection and try saving again.' : '';
}
