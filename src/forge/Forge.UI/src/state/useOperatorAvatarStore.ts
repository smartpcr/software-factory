import { create } from 'zustand';

// Vite glob — all PNGs under src/assets/crews/.
const CREWS_URLS = import.meta.glob<{ default: string }>(
  '@/assets/crews/*.png',
  { eager: true },
);

export interface AvatarEntry {
  filename: string;
  url: string;
  displayName: string;
  role: string;
}

// Parse `09_Taiji_Zhenrens_Disciple.png`
//   → { displayName: 'Taiji Zhenrens Disciple', role: '' }
// Convention: NN_FirstName_LastName_RolePascalCase.png. The role is always
// the trailing token; everything between the leading number and the role
// forms the display name.
function parseFilename(filename: string): { displayName: string; role: string } {
  const stem = filename.replace(/\.png$/i, '');
  const parts = stem.split('_');
  const tokens = /^\d+$/.test(parts[0] ?? '') ? parts.slice(1) : parts;
  if (tokens.length === 0) return { displayName: filename, role: '' };
  const role = tokens[tokens.length - 1].replace(/([a-z])([A-Z])/g, '$1 $2');
  const nameTokens = tokens.slice(0, -1);
  const displayName = nameTokens.length > 0 ? nameTokens.join(' ') : tokens[0];
  return { displayName, role };
}

export const CREWS_AVATARS: AvatarEntry[] = Object.entries(CREWS_URLS)
  .map(([path, mod]) => {
    const filename = path.split('/').pop() ?? path;
    const meta = parseFilename(filename);
    return { filename, url: mod.default, ...meta };
  })
  .sort((a, b) => a.filename.localeCompare(b.filename));

/** All available avatars. */
export const ALL_AVATARS: AvatarEntry[] = [...CREWS_AVATARS];

const DEFAULT_FILENAME = '01_Ne_Zha.png';
const STORAGE_KEY = 'forge.operator-avatar';

function resolveUrl(filename: string): string {
  const hit = ALL_AVATARS.find((a) => a.filename === filename);
  if (hit) return hit.url;
  // Crew avatar served by the API.
  if (filename.endsWith('.png')) return `/api/avatars/${filename}`;
  const fallback = ALL_AVATARS.find((a) => a.filename === DEFAULT_FILENAME);
  return fallback?.url ?? ALL_AVATARS[0]?.url ?? '';
}

function readInitial(): string {
  if (typeof window === 'undefined') return DEFAULT_FILENAME;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw && (ALL_AVATARS.some((a) => a.filename === raw) || raw.endsWith('.png'))) return raw;
  } catch { /* private mode / quota */ }
  return DEFAULT_FILENAME;
}

interface OperatorAvatarState {
  filename: string;
  url: string;
  setFilename: (filename: string) => void;
}

export const useOperatorAvatarStore = create<OperatorAvatarState>((set) => {
  const initial = readInitial();
  return {
    filename: initial,
    url: resolveUrl(initial),
    setFilename: (filename) => {
      const url = resolveUrl(filename);
      try { window.localStorage.setItem(STORAGE_KEY, filename); } catch { /* ignore */ }
      set({ filename, url });
    },
  };
});

/** React hook — re-renders when the operator picks a different avatar. */
export function useOperatorAvatarUrl(): string {
  return useOperatorAvatarStore((s) => s.url);
}

/** Non-hook getter — for module-scope consumers (e.g. agentAvatar.ts). */
export function getOperatorAvatarUrl(): string {
  return useOperatorAvatarStore.getState().url;
}