import type { Card, ColumnId, ForgeState } from '@/types';
import { sampleState } from '@/sampleData';

const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';

interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
}

export class ForgeApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: ProblemDetails | null,
    message: string,
  ) {
    super(message);
    this.name = 'ForgeApiError';
  }
}

function useLocalSeed(): boolean {
  if (typeof window === 'undefined') return false;
  return new URLSearchParams(window.location.search).get('seed') === 'local';
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${baseUrl}${path}`, {
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(init?.headers ?? {}),
    },
    ...init,
  });
  if (!res.ok) {
    let problem: ProblemDetails | null = null;
    const ct = res.headers.get('content-type') ?? '';
    if (ct.includes('json')) {
      try {
        problem = (await res.json()) as ProblemDetails;
      } catch {
        // fall through
      }
    }
    const detail = problem?.detail ?? problem?.title ?? res.statusText;
    throw new ForgeApiError(res.status, problem, `${path}: ${res.status} ${detail}`);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export interface MoveCardResponse {
  card: Card;
  policy: {
    allow: boolean;
    toast?: string | null;
    title?: string | null;
    text?: string | null;
    warn?: string | null;
    confirm?: boolean;
  };
}

export const forgeApi = {
  async getState(): Promise<ForgeState> {
    if (useLocalSeed()) {
      return sampleState;
    }
    try {
      return await fetchJson<ForgeState>('/api/state');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/state failed, using local sample data', err);
        return sampleState;
      }
      throw err;
    }
  },

  /** POST /api/cards. */
  async createCard(card: Card): Promise<Card> {
    return fetchJson<Card>('/api/cards', {
      method: 'POST',
      body: JSON.stringify({ card }),
    });
  },

  /** PATCH /api/cards/{id}. */
  async patchCard(id: string, patch: Partial<Card>): Promise<Card> {
    return fetchJson<Card>(`/api/cards/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(patch),
    });
  },

  /** POST /api/cards/{id}/move. */
  async moveCard(id: string, toCol: ColumnId, force = false): Promise<MoveCardResponse> {
    return fetchJson<MoveCardResponse>(`/api/cards/${encodeURIComponent(id)}/move`, {
      method: 'POST',
      body: JSON.stringify({ toCol, force }),
    });
  },
};
