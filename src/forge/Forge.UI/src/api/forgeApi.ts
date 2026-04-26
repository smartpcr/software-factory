import type {
  Card,
  ColumnId,
  Crew,
  CrewSalary,
  CurrentUser,
  DiscoverableRepo,
  ForgeState,
  Pr,
  Product,
  ProductStats,
  Repo,
  SalaryTodaySummary,
  TimelineResponse,
  UserSettings,
  Workstream,
} from '@/types';
import { sampleState } from '@/sampleData';

export interface ProviderHealth {
  kind: string;
  ok: boolean;
  message: string | null;
  lastSync: string | null;
  lastErrorCount: number;
  rateLimitRemaining: number | null;
  rateLimitLimit: number | null;
}

/** Phase 9 — interactive sign-in state for a provider. */
export interface AuthStatus {
  kind: string;
  connected: boolean;
  account: string | null;
  userName: string | null;
  expiresAt: string | null;
  error: string | null;
}

export interface AuditEntry {
  id: number;
  at: string;
  user: string;
  method: string;
  path: string;
  status: number;
  idempotencyKey: string | null;
  requestBodySnippet: string | null;
}

export interface HumanGateRules {
  branchSubstrings: string[];
  titleSubstrings: string[];
  labels: string[];
  pathGlobs: string[];
}

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

/** Phase 8.8 — active product the UI is scoped to (TopBar switcher). */
const ACTIVE_PRODUCT_KEY = 'forge.activeProductId';

export function getActiveProductId(): string | null {
  if (typeof window === 'undefined') return null;
  return window.localStorage.getItem(ACTIVE_PRODUCT_KEY);
}

export function setActiveProductId(id: string | null): void {
  if (typeof window === 'undefined') return;
  if (id === null) window.localStorage.removeItem(ACTIVE_PRODUCT_KEY);
  else window.localStorage.setItem(ACTIVE_PRODUCT_KEY, id);
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const { headers: extraHeaders, ...rest } = init ?? {};
  // Phase 8.9 — automatically scope every read to the active product when set.
  const productId = getActiveProductId();
  const res = await fetch(`${baseUrl}${path}`, {
    ...rest,
    headers: {
      Accept: 'application/json',
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(productId ? { 'X-Forge-Product-Id': productId } : {}),
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

  /** POST /api/cards. Phase 6.1: optional `postTo` posts to ADO/GitHub. */
  async createCard(card: Card, postTo?: 'ado' | 'github', description?: string): Promise<Card> {
    return fetchJson<Card>('/api/cards', {
      method: 'POST',
      body: JSON.stringify({ card, postTo, description }),
      headers: postTo ? { 'X-Forge-User': writerHeader() } : {},
    });
  },

  /** POST /api/cards/{id}/comment. */
  async commentOnCard(id: string, body: string, provider: 'ado' | 'github' = 'github'): Promise<void> {
    await fetchJson<unknown>(`/api/cards/${encodeURIComponent(id)}/comment`, {
      method: 'POST',
      body: JSON.stringify({ body, provider }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/approve. */
  async approvePr(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/approve`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/changes. */
  async requestChangesOnPr(id: string, body: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/changes`, {
      method: 'POST',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/comment. */
  async commentOnPr(id: string, body: string): Promise<void> {
    await fetchJson<unknown>(`/api/prs/${encodeURIComponent(id)}/comment`, {
      method: 'POST',
      body: JSON.stringify({ body }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/prs/{id}/merge. */
  async mergePr(id: string): Promise<Pr> {
    return fetchJson<Pr>(`/api/prs/${encodeURIComponent(id)}/merge`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/workstreams/{id}/pr — Phase 6.4. */
  async openWorkstreamPr(
    id: string,
    req: { title: string; body: string; provider?: 'ado' | 'github'; draft?: boolean },
  ): Promise<Pr> {
    return fetchJson<Pr>(`/api/workstreams/${encodeURIComponent(id)}/pr`, {
      method: 'POST',
      body: JSON.stringify({ provider: 'github', ...req }),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/audit. */
  async listAudit(take = 200): Promise<AuditEntry[]> {
    return fetchJson<AuditEntry[]>(`/api/audit?take=${take}`);
  },

  /** GET /api/human-gate/rules. */
  async getHumanGateRules(): Promise<HumanGateRules> {
    return fetchJson<HumanGateRules>('/api/human-gate/rules');
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

  /** GET /api/providers — last-known health snapshot for every provider. */
  async listProviders(): Promise<ProviderHealth[]> {
    if (useLocalSeed()) return [];
    try {
      return await fetchJson<ProviderHealth[]>('/api/providers');
    } catch (err) {
      if (import.meta.env.DEV) {
        console.warn('[forge] /api/providers failed', err);
        return [];
      }
      throw err;
    }
  },

  /** GET /api/providers/{kind}/test — live probe. */
  async testProvider(kind: string): Promise<ProviderHealth> {
    return fetchJson<ProviderHealth>(`/api/providers/${encodeURIComponent(kind)}/test`);
  },

  /** POST /api/providers/{kind}/sync — manual upsert run. Returns refreshed health. */
  async syncProviderNow(kind: string): Promise<ProviderHealth> {
    return fetchJson<ProviderHealth>(`/api/providers/${encodeURIComponent(kind)}/sync`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  // -- Phase 9 — interactive sign-in --------------------------------------

  /** GET /api/auth/{kind}/status. */
  async authStatus(kind: 'ado' | 'github'): Promise<AuthStatus> {
    return fetchJson<AuthStatus>(`/api/auth/${kind}/status`);
  },

  /** POST /api/auth/{kind}/sign-in — opens WAM/browser popup, blocks until done. */
  async signIn(kind: 'ado' | 'github'): Promise<AuthStatus> {
    return fetchJson<AuthStatus>(`/api/auth/${kind}/sign-in`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/auth/{kind}/sign-out — drops the cached account. */
  async signOut(kind: 'ado' | 'github'): Promise<void> {
    await fetchJson<void>(`/api/auth/${kind}/sign-out`, {
      method: 'POST',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/me — aggregated identity + per-provider auth state. */
  async getMe(): Promise<CurrentUser> {
    return fetchJson<CurrentUser>('/api/me');
  },

  // -- Phase 10 — preferences + crews ------------------------------------

  /** GET /api/me/settings. */
  async getMySettings(): Promise<UserSettings> {
    return fetchJson<UserSettings>('/api/me/settings');
  },

  /** PATCH /api/me/settings. */
  async patchMySettings(req: Partial<UserSettings>): Promise<UserSettings> {
    return fetchJson<UserSettings>('/api/me/settings', {
      method: 'PATCH',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** PATCH /api/repos/{id} — per-repo override editor. */
  async patchRepo(id: string, req: {
    displayName?: string;
    cloneRoot?: string;
    defaultBranch?: string;
    adoOrg?: string | null;
    adoProject?: string | null;
    ghOwner?: string | null;
    ghRepo?: string | null;
  }): Promise<Repo> {
    return fetchJson<Repo>(`/api/repos/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/crews. */
  async listCrews(productId?: string): Promise<Crew[]> {
    const qs = productId ? `?productId=${encodeURIComponent(productId)}` : '';
    return fetchJson<Crew[]>(`/api/crews${qs}`);
  },

  /** GET /api/crews/{id}/salary — Phase 11.9 salary roll-up. */
  async getCrewSalary(id: string): Promise<CrewSalary> {
    return fetchJson<CrewSalary>(`/api/crews/${encodeURIComponent(id)}/salary`);
  },

  /** GET /api/crews/salary/today — Phase 11.9 TopBar chip total. */
  async getSalaryTodayTotal(): Promise<SalaryTodaySummary> {
    return fetchJson<SalaryTodaySummary>('/api/crews/salary/today');
  },

  // ----- Phase 8 — products & repos --------------------------------------

  /** GET /api/products. */
  async listProducts(): Promise<Product[]> {
    return fetchJson<Product[]>('/api/products');
  },

  /** GET /api/products/{id}. */
  async getProduct(id: string): Promise<Product> {
    return fetchJson<Product>(`/api/products/${encodeURIComponent(id)}`);
  },

  /** GET /api/products/{id}/repos. */
  async listProductRepos(id: string): Promise<Repo[]> {
    return fetchJson<Repo[]>(`/api/products/${encodeURIComponent(id)}/repos`);
  },

  /** GET /api/products/{id}/stats — repo count + activity roll-up. */
  async getProductStats(id: string): Promise<ProductStats> {
    return fetchJson<ProductStats>(`/api/products/${encodeURIComponent(id)}/stats`);
  },

  /** POST /api/products. */
  async createProduct(req: {
    id: string;
    name: string;
    slug?: string;
    description?: string;
    ownerUserId?: string | null;
  }): Promise<Product> {
    return fetchJson<Product>('/api/products', {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** POST /api/products/{id}/repos. */
  async attachRepo(productId: string, req: {
    id: string;
    kind: 'ado' | 'github';
    remoteUrl: string;
    displayName: string;
    cloneRoot: string;
    defaultBranch?: string;
    adoOrg?: string;
    adoProject?: string;
    ghOwner?: string;
    ghRepo?: string;
  }): Promise<Repo> {
    return fetchJson<Repo>(`/api/products/${encodeURIComponent(productId)}/repos`, {
      method: 'POST',
      body: JSON.stringify(req),
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/repos[?productId=…]. */
  async listRepos(productId?: string): Promise<Repo[]> {
    const qs = productId ? `?productId=${encodeURIComponent(productId)}` : '';
    return fetchJson<Repo[]>(`/api/repos${qs}`);
  },

  /** DELETE /api/repos/{id}?force=true. */
  async deleteRepo(id: string, force = false): Promise<void> {
    await fetchJson<void>(`/api/repos/${encodeURIComponent(id)}?force=${force}`, {
      method: 'DELETE',
      headers: { 'X-Forge-User': writerHeader() },
    });
  },

  /** GET /api/providers/{kind}/repos — Phase 8.5 server-driven add-repo picker. */
  async discoverProviderRepos(kind: 'ado' | 'github'): Promise<DiscoverableRepo[]> {
    return fetchJson<DiscoverableRepo[]>(`/api/providers/${encodeURIComponent(kind)}/repos`);
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
