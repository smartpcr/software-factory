import { create } from 'zustand';
import type { CurrentUser } from '@/types';
import { forgeApi, setWriterIdentity } from '@/api/forgeApi';
import imgXiaodong from '@/assets/crews/xiaodong_small.png';

// Operator portrait override. The /api/me endpoint can store an
// AvatarUrl on the user row; until that's wired through Phase 9 sign-in,
// match the operator on email/login and bind to the bundled crew portrait
// so the TopBar + SideBar show a real face instead of computed initials.
const OPERATOR_AVATARS: Array<{ match: (u: CurrentUser) => boolean; avatar: string }> = [
  {
    match: (u) =>
      u.email?.toLowerCase() === 'xiaodoli@microsoft.com'
      || u.email?.toLowerCase() === 'lingxd@gmail.com'
      || u.login?.toLowerCase() === 'xiaodoli'
      || u.displayName?.toLowerCase().startsWith('xiaodong') === true,
    avatar: imgXiaodong,
  },
];

function applyOperatorAvatar(user: CurrentUser): CurrentUser {
  if (user.avatarUrl) return user; // server-provided wins
  const hit = OPERATOR_AVATARS.find((entry) => entry.match(user));
  return hit ? { ...user, avatarUrl: hit.avatar } : user;
}

interface CurrentUserState {
  user: CurrentUser | null;
  loaded: boolean;
  load: () => Promise<void>;
  refresh: () => Promise<void>;
}

const PLACEHOLDER: CurrentUser = {
  id: '',
  displayName: 'Sign in',
  email: null,
  login: null,
  initials: '·',
  avatarUrl: null,
  role: 'human',
  providers: {
    ado: { connected: false, account: null, expiresAt: null, error: null },
    github: { connected: false, account: null, expiresAt: null, error: null },
  },
};

// Phase 9.6/9.7 — central source of truth for the signed-in user. The
// TopBar avatar and SideBar block read from this; sign-in / sign-out paths
// call refresh() so identity updates without a page reload.
export const useCurrentUserStore = create<CurrentUserState>((set) => ({
  user: null,
  loaded: false,

  async load() {
    try {
      const user = await forgeApi.getMe();
      pushWriterIdentity(user);
      set({ user: applyOperatorAvatar(user), loaded: true });
    } catch {
      set({ user: PLACEHOLDER, loaded: true });
    }
  },

  async refresh() {
    try {
      const user = await forgeApi.getMe();
      pushWriterIdentity(user);
      set({ user: applyOperatorAvatar(user) });
    } catch {
      // Keep last good user — refresh is fire-and-forget.
    }
  },
}));

// Phase 9.6 — sync the resolved identity into the API client so every
// write request lands `X-Forge-User: <email|login|id>` instead of the
// pre-sign-in placeholder. Without this, RequireRole.ResolveRole can't
// match the user row and falls back to "human", which 403s every PO
// action regardless of the role saved in the DB.
function pushWriterIdentity(user: CurrentUser) {
  const identity = user.email ?? user.login ?? user.id;
  if (identity && identity.length > 0) setWriterIdentity(identity);
}

export function useCurrentUser(): CurrentUser {
  const user = useCurrentUserStore((s) => s.user);
  return user ?? PLACEHOLDER;
}

/**
 * Phase 10.10 — UI role gate. Returns true only when the loaded user's role
 * matches. Truly unsigned users (loaded but no provider connected → id === '')
 * are gated out so the role-restricted UI surfaces stay hidden until sign-in
 * completes. Pre-load (loaded === false) also returns false, so gated UI
 * doesn't flash on first render and then disappear once /api/me resolves.
 */
export function useHasRole(...roles: string[]): boolean {
  const user = useCurrentUserStore((s) => s.user);
  const loaded = useCurrentUserStore((s) => s.loaded);
  if (!loaded || !user || user.id === '') return false;
  return roles.includes(user.role);
}
