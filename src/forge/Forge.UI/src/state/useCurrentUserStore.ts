import { create } from 'zustand';
import type { CurrentUser } from '@/types';
import { forgeApi } from '@/api/forgeApi';

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
      set({ user, loaded: true });
    } catch {
      set({ user: PLACEHOLDER, loaded: true });
    }
  },

  async refresh() {
    try {
      const user = await forgeApi.getMe();
      set({ user });
    } catch {
      // Keep last good user — refresh is fire-and-forget.
    }
  },
}));

export function useCurrentUser(): CurrentUser {
  const user = useCurrentUserStore((s) => s.user);
  return user ?? PLACEHOLDER;
}

/**
 * Phase 10.10 — UI role gate. Returns true when the current user's role is
 * one of the listed roles. In single-user dev (no signed-in user yet) we
 * return true so the operator isn't locked out of localhost development.
 */
export function useHasRole(...roles: string[]): boolean {
  const user = useCurrentUserStore((s) => s.user);
  // Pre-load placeholder: no user yet — pass-through.
  if (!user || user.id === '') return true;
  return roles.includes(user.role);
}
