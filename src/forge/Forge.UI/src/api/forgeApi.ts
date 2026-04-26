import type { Card, ColumnId, ForgeState, Pr, TimelineResponse, Workstream } from '@/types';
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

/** Phase-3 placeholder for the auth identity. Phase 5 replaces this. */
function writerHeader(): string {
  if (typeof window === 'undefined') return 'forge-ui';
  return window.localStorage.getItem('forge.user') ?? 'forge-ui';
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const { headers: extraHeaders, ...rest } = init ?? {};
  const res = await fetch(`${baseUrl}${path}`, {
    ...rest,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(extraHeaders ?? {}),
    },
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

export interface PrDetailsResponse {
  pr: Pr;
  description: string;
  buildLogTail: string[];
  reviewers: string[];
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

  /** GET /api/workstreams. */
  async getWorkstreams(): Promise<Workstream[]> {
    if (useLocalSeed()) return sampleState.workstreams;
    try {
      return await fetchJson<Workstream[]>('/api/workstreams');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/workstreams failed, using local sample data', err);
        return sampleState.workstreams;
      }
      throw err;
    }
  },

  /** POST /api/workstreams. */
  async createWorkstream(req: {
    id: string;
    name: string;
    branch: string;
    base: string;
    epic: string;
    worktreePath?: string | null;
  }): Promise<Workstream> {
    return fetchJson<Workstream>('/api/workstreams', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/workstreams/{id}/sync. */
  async syncWorkstream(id: string): Promise<Workstream> {
    return fetchJson<Workstream>(`/api/workstreams/${encodeURIComponent(id)}/sync`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** DELETE /api/workstreams/{id}?force=true. */
  async deleteWorkstream(id: string, force = false): Promise<void> {
    await fetchJson<void>(`/api/workstreams/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/prs. */
  async getPrs(): Promise<Pr[]> {
    if (useLocalSeed()) return sampleState.prs;
    try {
      return await fetchJson<Pr[]>('/api/prs');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/prs failed, using local sample data', err);
        return sampleState.prs;
      }
      throw err;
    }
  },

  /** GET /api/prs/{id}/details. */
  async getPrDetails(id: string): Promise<PrDetailsResponse> {
    return fetchJson<PrDetailsResponse>(`/api/prs/${encodeURIComponent(id)}/details`);
  },

  /** POST /api/prs/{id}/auto-merge. */
  async setAutoMerge(id: string, autoMerge: boolean): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/auto-merge`, {
      method: 'POST',
      body: JSON.stringify({ autoMerge }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/build/rerun. */
  async rerunBuild(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/build/rerun`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/build/cancel. */
  async cancelBuild(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/build/cancel`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/timeline?from=…&to=… . */
  async getTimeline(from = -10, to = 5): Promise<TimelineResponse> {
    if (useLocalSeed()) {
      return buildLocalTimeline(from, to);
    }
    try {
      return await fetchJson<TimelineResponse>(`/api/timeline?from=${from}&to=${to}`);
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/timeline failed, using local sample data', err);
        return buildLocalTimeline(from, to);
      }
      throw err;
    }
  },
};

function buildLocalTimeline(from: number, to: number): TimelineResponse {
  const epicColor = new Map(sampleState.epics.map((e) => [e.id, e.color]));
  const bars = sampleState.workstreams
    .filter((w) => !(w.barEnd < from || w.barStart > to))
    .map((w) => ({
      workstreamId: w.id,
      name: w.name,
      branch: w.branch,
      epic: w.epic,
      epicColor: epicColor.get(w.epic) ?? w.epic,
      status: w.status,
      barStart: w.barStart,
      barEnd: w.barEnd,
      planned: !!w.planned,
      summary: w.summary,
      agents: w.agents,
      ahead: w.ahead,
      behind: w.behind,
      conflicts: w.conflicts,
      lastActive: w.lastActive,
    }));
  const markers = sampleState.wsEvents
    .filter((e) => e.day >= from && e.day <= to)
    .map((e) => ({ workstreamId: e.ws, day: e.day, kind: e.kind, label: e.label }));
  return { from, to, bars, markers };
}
