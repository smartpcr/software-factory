// -----------------------------------------------------------------------
// Phase 23/24 — RepoSyncPanel. A comprehensive right-rail panel for
// managing work-item and PR sync configuration per repo. Replaces the
// scattered sync status strip + edit modal pattern with a single unified
// surface that lets the operator configure filters, trigger syncs, and
// inspect history + errors in one place.
// -----------------------------------------------------------------------

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type { Repo, WorkItemSyncRun, SyncRunItem } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useCurrentUser } from '@/state/useCurrentUserStore';
import { relativeTime, statusDotColor } from '@/views/RepoSyncStatusStrip';

type Tab = 'workitems' | 'prs' | 'history';

const KNOWN_TEAMS = ['Engineering', 'Architecture', 'Review', 'QA'] as const;

interface Props {
  repo: Repo;
  open: boolean;
  onClose: () => void;
  onRepoUpdated?: (updated: Repo) => void;
}

// ── Work Item Sync Filters ──────────────────────────────────────────────

interface WiSyncFilters {
  areaPaths: string;
  assignedTo: string;
  fromDate: string;
  toDate: string;
  agentReadyTeam: string;
  syncKind: 'auto' | 'full' | 'delta';
}

// ── PR Sync Filters ─────────────────────────────────────────────────────

interface PrSyncFilters {
  createdBy: string;
  fromDate: string;
  toDate: string;
  associatedWorkItem: string;
  stateFilter: 'active' | 'all' | 'completed';
}

// ── Persistence helpers ─────────────────────────────────────────────────

const STORAGE_PREFIX = 'forge-sync-filters-';

function loadFilters<T>(repoId: string, kind: string, defaults: T): T {
  try {
    const raw = localStorage.getItem(`${STORAGE_PREFIX}${kind}-${repoId}`);
    if (!raw) return defaults;
    return { ...defaults, ...JSON.parse(raw) };
  } catch { return defaults; }
}

function saveFilters(repoId: string, kind: string, filters: unknown) {
  try {
    localStorage.setItem(`${STORAGE_PREFIX}${kind}-${repoId}`, JSON.stringify(filters));
  } catch { /* quota exceeded — best effort */ }
}

function isoDate(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function defaultFromDate(): string {
  const d = new Date();
  d.setDate(d.getDate() - 30);
  return isoDate(d);
}

function defaultToDate(): string {
  return isoDate(new Date());
}

// ── Exported Panel ──────────────────────────────────────────────────────

export function RepoSyncPanel({ repo, open, onClose, onRepoUpdated }: Props) {
  const [tab, setTab] = useState<Tab>('workitems');
  const [saving, setSaving] = useState(false);
  const toast = useToastStore((s) => s.show);
  const me = useCurrentUser();

  // Work item sync state — loaded from localStorage with smart defaults
  const wiDefaults: WiSyncFilters = {
    areaPaths: (repo.areaPaths ?? []).join('\n'),
    assignedTo: me.email ?? me.displayName ?? '',
    fromDate: defaultFromDate(),
    toDate: defaultToDate(),
    agentReadyTeam: repo.agentReadyDefaultTeam ?? 'Engineering',
    syncKind: 'auto',
  };
  const [wiFilters, setWiFilters] = useState<WiSyncFilters>(() =>
    loadFilters(repo.id, 'wi', wiDefaults),
  );

  // PR sync state — loaded from localStorage with smart defaults
  const prDefaults: PrSyncFilters = {
    createdBy: me.email ?? me.displayName ?? '',
    fromDate: defaultFromDate(),
    toDate: defaultToDate(),
    associatedWorkItem: '',
    stateFilter: 'active',
  };
  const [prFilters, setPrFilters] = useState<PrSyncFilters>(() =>
    loadFilters(repo.id, 'pr', prDefaults),
  );

  // History state
  const [runs, setRuns] = useState<WorkItemSyncRun[] | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);

  // Sync in progress
  const [syncing, setSyncing] = useState(false);

  // Reset state when repo changes or panel opens — merge server-persisted
  // values (areaPaths, agentReadyTeam) with localStorage-persisted filters
  useEffect(() => {
    if (!open) return;
    const freshWiDefaults: WiSyncFilters = {
      areaPaths: (repo.areaPaths ?? []).join('\n'),
      assignedTo: me.email ?? me.displayName ?? '',
      fromDate: defaultFromDate(),
      toDate: defaultToDate(),
      agentReadyTeam: repo.agentReadyDefaultTeam ?? 'Engineering',
      syncKind: 'auto',
    };
    const loaded = loadFilters(repo.id, 'wi', freshWiDefaults);
    // Always take server-persisted values fresh from repo
    loaded.areaPaths = (repo.areaPaths ?? []).join('\n');
    loaded.agentReadyTeam = repo.agentReadyDefaultTeam ?? loaded.agentReadyTeam;
    setWiFilters(loaded);

    const freshPrDefaults: PrSyncFilters = {
      createdBy: me.email ?? me.displayName ?? '',
      fromDate: defaultFromDate(),
      toDate: defaultToDate(),
      associatedWorkItem: '',
      stateFilter: 'active',
    };
    setPrFilters(loadFilters(repo.id, 'pr', freshPrDefaults));
  }, [open, repo.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // Persist filters to localStorage on change
  useEffect(() => {
    if (!open) return;
    saveFilters(repo.id, 'wi', wiFilters);
  }, [open, repo.id, wiFilters]);

  useEffect(() => {
    if (!open) return;
    saveFilters(repo.id, 'pr', prFilters);
  }, [open, repo.id, prFilters]);

  // Load history when tab switches to history
  useEffect(() => {
    if (!open || tab !== 'history') return;
    let cancelled = false;
    setHistoryLoading(true);
    setHistoryError(null);
    fetch(`/api/repos/${encodeURIComponent(repo.id)}/sync-work-items/runs?limit=50`)
      .then((r) => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
      .then((rs: WorkItemSyncRun[]) => { if (!cancelled) setRuns(rs); })
      .catch((e: Error) => { if (!cancelled) setHistoryError(e.message); })
      .finally(() => { if (!cancelled) setHistoryLoading(false); });
    return () => { cancelled = true; };
  }, [open, tab, repo.id]);

  // Keyboard + scroll lock
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !saving && !syncing) onClose(); };
    window.addEventListener('keydown', onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = prev;
    };
  }, [open, onClose, saving, syncing]);

  if (!open) return null;

  const status = repo.lastSyncStatus ?? 'idle';
  const dotColor = statusDotColor(status);
  const isAdo = repo.kind === 'ado';

  // ── Save sync config ────────────────────────────────────────────────

  async function saveConfig() {
    setSaving(true);
    try {
      const areaPaths = wiFilters.areaPaths
        .split(/\r?\n/)
        .map((s) => s.trim())
        .filter((s) => s.length > 0);
      const team = wiFilters.agentReadyTeam.trim() || null;
      const updated = await forgeApi.patchRepo(repo.id, {
        areaPaths,
        agentReadyDefaultTeam: team,
      });
      onRepoUpdated?.(updated);
      toast('Sync configuration saved', 'success');
    } catch (e) {
      toast(e instanceof Error ? e.message : 'Save failed', 'danger');
    } finally {
      setSaving(false);
    }
  }

  // ── Trigger sync ────────────────────────────────────────────────────

  async function triggerWiSync() {
    setSyncing(true);
    try {
      await forgeApi.syncRepoWorkItems(repo.id, { kind: wiFilters.syncKind });
      toast(`Work item sync started (${wiFilters.syncKind})`, 'success');
      // Refresh history if on that tab
      if (tab === 'history') {
        setRuns(null);
        const rs = await fetch(`/api/repos/${encodeURIComponent(repo.id)}/sync-work-items/runs?limit=50`)
          .then((r) => r.json());
        setRuns(rs);
      }
    } catch (e) {
      toast(e instanceof Error ? e.message : 'Sync failed', 'danger');
    } finally {
      setSyncing(false);
    }
  }

  async function triggerPrSync() {
    setSyncing(true);
    try {
      const result = await forgeApi.syncPrs({
        repoId: repo.id,
        provider: repo.kind,
        createdBy: prFilters.createdBy || null,
        status: prFilters.stateFilter === 'all' ? null : prFilters.stateFilter,
        fromDate: prFilters.fromDate || null,
        toDate: prFilters.toDate || null,
      });
      const summary = `${result.pulled} pulled · ${result.inserted} new · ${result.updated} updated`;
      toast(`PR sync completed · ${summary}`, 'success');
    } catch (e) {
      toast(e instanceof Error ? e.message : 'PR sync failed', 'danger');
    } finally {
      setSyncing(false);
    }
  }

  return createPortal(
    <>
      {/* Backdrop */}
      <div
        aria-hidden
        onClick={onClose}
        style={{
          position: 'fixed',
          inset: 0,
          background: 'rgba(0, 0, 0, 0.4)',
          backdropFilter: 'blur(2px)',
          zIndex: 9000,
          animation: 'syncPanelFadeIn 180ms ease-out',
        }}
      />
      {/* Panel */}
      <div
        role="dialog"
        aria-modal="true"
        data-testid={`repo-sync-panel-${repo.id}`}
        onClick={(e) => e.stopPropagation()}
        style={{
          position: 'fixed',
          top: 0,
          right: 0,
          bottom: 0,
          width: 580,
          maxWidth: '100vw',
          background: 'var(--bg-elevated)',
          color: 'var(--text-primary)',
          borderLeft: '1px solid var(--border)',
          boxShadow: '-12px 0 40px rgba(0,0,0,0.25)',
          zIndex: 9001,
          display: 'flex',
          flexDirection: 'column',
          animation: 'syncPanelSlideIn 220ms cubic-bezier(0.16, 1, 0.3, 1)',
        }}
      >
        {/* ── Header ─────────────────────────────────────────────── */}
        <header style={{
          padding: '16px 20px',
          borderBottom: '1px solid var(--border)',
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          flexShrink: 0,
        }}>
          <div style={{
            width: 36, height: 36, borderRadius: 'var(--radius-md)',
            background: 'var(--accent-bg)',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            flexShrink: 0,
          }}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.78 7.78 5.5 5.5 0 0 1 7.78-7.78z"/>
              <path d="M15.5 8.5l3-3m-3 3L19 12m-3.5-3.5L12 5"/>
            </svg>
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{
              fontFamily: 'var(--font-display)',
              fontWeight: 700,
              fontSize: 15,
              letterSpacing: -0.2,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}>
              {repo.displayName}
            </div>
            <div style={{
              fontSize: 11,
              color: 'var(--text-tertiary)',
              display: 'flex',
              alignItems: 'center',
              gap: 6,
              marginTop: 2,
            }}>
              <span style={{
                display: 'inline-block',
                width: 7, height: 7,
                borderRadius: 999,
                background: dotColor,
                boxShadow: status === 'running' ? `0 0 0 3px color-mix(in srgb, ${dotColor} 30%, transparent)` : 'none',
              }} />
              <span>{status === 'idle' ? 'Last sync' : status === 'running' ? 'Syncing' : 'Failed'}</span>
              <span>·</span>
              <span>{relativeTime(repo.lastSyncAt)}</span>
              <span>·</span>
              <span style={{ textTransform: 'uppercase', fontSize: 10, fontWeight: 600, letterSpacing: 0.5 }}>
                {isAdo ? 'ADO' : 'GitHub'}
              </span>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            style={{
              width: 28, height: 28,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: 'transparent', border: '1px solid var(--border)',
              borderRadius: 'var(--radius-sm)',
              cursor: 'pointer', color: 'var(--text-tertiary)',
              transition: 'all 120ms',
            }}
            onMouseEnter={(e) => { e.currentTarget.style.background = 'var(--danger-bg)'; e.currentTarget.style.color = 'var(--danger)'; e.currentTarget.style.borderColor = 'var(--danger-border)'; }}
            onMouseLeave={(e) => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = 'var(--text-tertiary)'; e.currentTarget.style.borderColor = 'var(--border)'; }}
          >
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M3 3L9 9M9 3L3 9" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round"/></svg>
          </button>
        </header>

        {/* ── Error banner ───────────────────────────────────────── */}
        {status === 'failed' && repo.lastSyncError && (
          <div style={{
            padding: '10px 20px',
            background: 'var(--danger-bg)',
            borderBottom: '1px solid var(--danger-border)',
            fontSize: 12,
            lineHeight: 1.5,
            display: 'flex',
            gap: 8,
            alignItems: 'flex-start',
          }}>
            <span style={{ color: 'var(--danger)', fontWeight: 700, fontSize: 13, lineHeight: 1.3, flexShrink: 0 }}>⚠</span>
            <div>
              <div style={{ color: 'var(--danger)', fontWeight: 600, marginBottom: 2 }}>Sync failed</div>
              <div style={{
                color: 'var(--text-secondary)',
                fontFamily: 'var(--font-mono)',
                fontSize: 11,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                maxHeight: 80,
                overflow: 'auto',
              }}>
                {repo.lastSyncError}
              </div>
            </div>
          </div>
        )}

        {/* ── Tab bar ────────────────────────────────────────────── */}
        <div style={{
          display: 'flex',
          borderBottom: '1px solid var(--border)',
          padding: '0 20px',
          gap: 0,
          flexShrink: 0,
        }}>
          {(['workitems', 'prs', 'history'] as Tab[]).map((t) => {
            const labels: Record<Tab, string> = { workitems: 'Work Items', prs: 'Pull Requests', history: 'Sync History' };
            const icons: Record<Tab, string> = { workitems: '⚙', prs: '⤴', history: '◷' };
            const active = tab === t;
            return (
              <button
                key={t}
                type="button"
                onClick={() => setTab(t)}
                style={{
                  padding: '10px 16px',
                  fontSize: 12,
                  fontWeight: active ? 700 : 500,
                  fontFamily: 'var(--font-body)',
                  color: active ? 'var(--accent)' : 'var(--text-tertiary)',
                  background: 'transparent',
                  border: 'none',
                  borderBottom: active ? '2px solid var(--accent)' : '2px solid transparent',
                  cursor: 'pointer',
                  transition: 'all 150ms',
                  letterSpacing: 0.1,
                  display: 'flex',
                  alignItems: 'center',
                  gap: 5,
                }}
              >
                <span style={{ fontSize: 13 }}>{icons[t]}</span>
                {labels[t]}
              </button>
            );
          })}
        </div>

        {/* ── Tab content ────────────────────────────────────────── */}
        <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
          {tab === 'workitems' && (
            <WorkItemSyncTab
              repo={repo}
              filters={wiFilters}
              onChange={setWiFilters}
              isAdo={isAdo}
              saving={saving}
              syncing={syncing}
              onSave={saveConfig}
              onSync={triggerWiSync}
            />
          )}
          {tab === 'prs' && (
            <PrSyncTab
              repo={repo}
              filters={prFilters}
              onChange={setPrFilters}
              isAdo={isAdo}
              syncing={syncing}
              onSync={triggerPrSync}
            />
          )}
          {tab === 'history' && (
            <HistoryTab
              runs={runs}
              loading={historyLoading}
              error={historyError}
              repoId={repo.id}
            />
          )}
        </div>
      </div>

      <style>{`
        @keyframes syncPanelSlideIn {
          from { transform: translateX(100%); opacity: 0; }
          to { transform: translateX(0); opacity: 1; }
        }
        @keyframes syncPanelFadeIn {
          from { opacity: 0; }
          to { opacity: 1; }
        }
      `}</style>
    </>,
    document.body,
  );
}

// ═══════════════════════════════════════════════════════════════════════════
// ── Work Item Sync Tab ────────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════════════

function WorkItemSyncTab({
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  repo: _repo,
  filters,
  onChange,
  isAdo,
  saving,
  syncing,
  onSave,
  onSync,
}: {
  repo: Repo;
  filters: WiSyncFilters;
  onChange: (f: WiSyncFilters) => void;
  isAdo: boolean;
  saving: boolean;
  syncing: boolean;
  onSave: () => void;
  onSync: () => void;
}) {
  return (
    <div style={{ display: 'grid', gap: 20 }}>
      {/* Sync config section */}
      <SectionHeader icon="⛏" title="Sync Filters" subtitle="Configure which work items to pull from upstream" />

      <div style={{ display: 'grid', gap: 14 }}>
        {isAdo && (
          <PanelField
            label="Area Paths"
            hint="One per line. Uses UNDER prefix in WIQL. Empty = whole project."
          >
            <textarea
              value={filters.areaPaths}
              onChange={(e) => onChange({ ...filters, areaPaths: e.target.value })}
              placeholder={'One\\AzureStack\\ASZ-Update\nOne\\AzureStack\\Compute'}
              rows={4}
              style={{ ...fieldInputStyle, fontFamily: 'var(--font-mono)', fontSize: 11.5, resize: 'vertical' }}
              data-testid="sync-area-paths"
            />
          </PanelField>
        )}

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
          <PanelField label="Updated from" hint="Lower bound for ChangedDate / updatedAt">
            <input
              type="date"
              value={filters.fromDate}
              onChange={(e) => onChange({ ...filters, fromDate: e.target.value })}
              style={fieldInputStyle}
            />
          </PanelField>
          <PanelField label="Updated to" hint="Upper bound">
            <input
              type="date"
              value={filters.toDate}
              onChange={(e) => onChange({ ...filters, toDate: e.target.value })}
              style={fieldInputStyle}
            />
          </PanelField>
        </div>

        <PanelField label="Assigned to" hint="Filter by assignee email / UPN. Empty = everyone.">
          <input
            type="text"
            value={filters.assignedTo}
            onChange={(e) => onChange({ ...filters, assignedTo: e.target.value })}
            placeholder="someone@your-org.com"
            style={fieldInputStyle}
          />
        </PanelField>

        <PanelField
          label="Agent-ready default team"
          hint="Items tagged 'agent-ready' upstream auto-assign to this team."
        >
          <select
            value={filters.agentReadyTeam}
            onChange={(e) => onChange({ ...filters, agentReadyTeam: e.target.value })}
            style={{
              ...fieldInputStyle,
              appearance: 'none',
              backgroundImage: `url("data:image/svg+xml,${encodeURIComponent('<svg width="10" height="6" viewBox="0 0 10 6" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M1 1l4 4 4-4" stroke="%23666" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>')}")`,
              backgroundRepeat: 'no-repeat',
              backgroundPosition: 'right 10px center',
              paddingRight: 28,
            }}
            data-testid="sync-agent-ready-team"
          >
            {KNOWN_TEAMS.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
        </PanelField>
      </div>

      <SectionHeader icon="▶" title="Run Sync" subtitle="Trigger a sync run with the current configuration" />

      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <select
          value={filters.syncKind}
          onChange={(e) => onChange({ ...filters, syncKind: e.target.value as WiSyncFilters['syncKind'] })}
          style={{
            ...fieldInputStyle,
            width: 'auto',
            padding: '7px 28px 7px 10px',
            appearance: 'none',
            backgroundImage: `url("data:image/svg+xml,${encodeURIComponent('<svg width="10" height="6" viewBox="0 0 10 6" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M1 1l4 4 4-4" stroke="%23666" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>')}")`,
            backgroundRepeat: 'no-repeat',
            backgroundPosition: 'right 10px center',
          }}
        >
          <option value="auto">Auto (delta if possible)</option>
          <option value="delta">Delta only</option>
          <option value="full">Full re-sync</option>
        </select>
        <button
          type="button"
          onClick={onSync}
          disabled={syncing}
          style={{
            ...actionBtnStyle,
            background: 'var(--accent)',
            color: 'var(--text-on-accent)',
            opacity: syncing ? 0.6 : 1,
          }}
        >
          {syncing ? '⟳ Running…' : '▶ Sync now'}
        </button>
      </div>

      {/* Save config */}
      <div style={{ borderTop: '1px solid var(--border)', paddingTop: 16, display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
        <button
          type="button"
          onClick={onSave}
          disabled={saving}
          style={{
            ...actionBtnStyle,
            background: 'var(--bg-surface-2)',
            color: 'var(--text-primary)',
            border: '1px solid var(--border-strong)',
            opacity: saving ? 0.6 : 1,
          }}
        >
          {saving ? 'Saving…' : 'Save configuration'}
        </button>
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════
// ── PR Sync Tab ───────────────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════════════

function PrSyncTab({
  repo: _repo,
  filters,
  onChange,
  isAdo,
  syncing,
  onSync,
}: {
  repo: Repo;
  filters: PrSyncFilters;
  onChange: (f: PrSyncFilters) => void;
  isAdo: boolean;
  syncing: boolean;
  onSync: () => void;
}) {
  return (
    <div style={{ display: 'grid', gap: 20 }}>
      <SectionHeader icon="⤴" title="PR Sync Filters" subtitle="Configure which pull requests to track" />

      <div style={{ display: 'grid', gap: 14 }}>
        <PanelField
          label="Created by"
          hint={isAdo ? 'ADO unique name or display name' : 'GitHub login'}
        >
          <input
            type="text"
            value={filters.createdBy}
            onChange={(e) => onChange({ ...filters, createdBy: e.target.value })}
            placeholder="Filter by author…"
            style={fieldInputStyle}
          />
        </PanelField>

        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
          <PanelField label="Created from">
            <input
              type="date"
              value={filters.fromDate}
              onChange={(e) => onChange({ ...filters, fromDate: e.target.value })}
              style={fieldInputStyle}
            />
          </PanelField>
          <PanelField label="Created to">
            <input
              type="date"
              value={filters.toDate}
              onChange={(e) => onChange({ ...filters, toDate: e.target.value })}
              style={fieldInputStyle}
            />
          </PanelField>
        </div>

        <PanelField
          label="Associated work item"
          hint="Only show PRs linked to this work item ID"
        >
          <input
            type="text"
            value={filters.associatedWorkItem}
            onChange={(e) => onChange({ ...filters, associatedWorkItem: e.target.value })}
            placeholder="e.g. WI-1234"
            style={fieldInputStyle}
          />
        </PanelField>

        <PanelField label="State filter" hint="Which PR states to include in sync">
          <div style={{ display: 'flex', gap: 6 }}>
            {(['active', 'completed', 'all'] as const).map((s) => {
              const active = filters.stateFilter === s;
              return (
                <button
                  key={s}
                  type="button"
                  onClick={() => onChange({ ...filters, stateFilter: s })}
                  style={{
                    padding: '5px 12px',
                    fontSize: 11.5,
                    fontWeight: active ? 700 : 500,
                    fontFamily: 'var(--font-body)',
                    color: active ? 'var(--text-on-accent)' : 'var(--text-secondary)',
                    background: active ? 'var(--accent)' : 'var(--bg-surface-2)',
                    border: active ? 'none' : '1px solid var(--border)',
                    borderRadius: 'var(--radius-sm)',
                    cursor: 'pointer',
                    transition: 'all 120ms',
                    textTransform: 'capitalize',
                  }}
                >
                  {s}
                </button>
              );
            })}
          </div>
        </PanelField>
      </div>

      {/* Sync action */}
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 12,
        padding: 14,
        background: 'var(--accent-bg)',
        borderRadius: 'var(--radius-md)',
        border: '1px solid var(--accent)',
      }}>
        <button
          type="button"
          onClick={onSync}
          disabled={syncing}
          style={{
            padding: '7px 16px',
            fontSize: 12,
            fontWeight: 600,
            fontFamily: 'var(--font-body)',
            color: 'var(--text-on-accent)',
            background: 'var(--accent)',
            border: 'none',
            borderRadius: 'var(--radius-sm)',
            cursor: syncing ? 'wait' : 'pointer',
            opacity: syncing ? 0.7 : 1,
            display: 'inline-flex',
            alignItems: 'center',
            gap: 6,
          }}
        >
          <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
            <path d="M2 6 a 4 4 0 0 1 7 -2.5 M 10 6 a 4 4 0 0 1 -7 2.5 M 9 1.5 V 3.5 H 7 M 3 10.5 V 8.5 H 5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {syncing ? 'Syncing…' : `Sync PRs from ${isAdo ? 'ADO' : 'GitHub'}`}
        </button>
        <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
          Pull PRs matching filters above into local cache
        </span>
      </div>

      {/* PR sync status summary */}
      <div style={{
        padding: 14,
        background: 'var(--bg-surface-2)',
        borderRadius: 'var(--radius-md)',
        border: '1px solid var(--border)',
        fontSize: 12,
        color: 'var(--text-secondary)',
        lineHeight: 1.6,
      }}>
        <div style={{ fontWeight: 600, color: 'var(--text-primary)', marginBottom: 4, fontSize: 11.5 }}>
          PR sync also runs automatically every 20 minutes
        </div>
        Active PRs are tracked until they reach a terminal state (completed or abandoned).
        Comments are synced along with PR metadata. The sync cadence is configurable
        via <code style={{ fontSize: 10.5, fontFamily: 'var(--font-mono)', background: 'var(--bg-sunken)', padding: '1px 4px', borderRadius: 2 }}>Forge:Sync:Pr:IntervalSeconds</code>.
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════
// ── History Tab ───────────────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════════════

function HistoryTab({
  runs,
  loading,
  error,
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  repoId: _repoId,
}: {
  runs: WorkItemSyncRun[] | null;
  loading: boolean;
  error: string | null;
  repoId: string;
}) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  function toggle(id: string) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  return (
    <div style={{ display: 'grid', gap: 16 }}>
      <SectionHeader icon="◷" title="Sync History" subtitle="Recent sync runs with results and errors" />

      {loading && (
        <div style={{ padding: 20, textAlign: 'center', color: 'var(--text-tertiary)', fontSize: 13 }}>
          Loading sync history…
        </div>
      )}

      {error && (
        <div style={{
          padding: 12,
          background: 'var(--danger-bg)',
          borderRadius: 'var(--radius-sm)',
          border: '1px solid var(--danger-border)',
          color: 'var(--danger)',
          fontSize: 12,
        }}>
          Failed to load history: {error}
        </div>
      )}

      {runs && runs.length === 0 && !loading && (
        <div style={{
          padding: 32,
          textAlign: 'center',
          color: 'var(--text-tertiary)',
          fontSize: 13,
          background: 'var(--bg-surface-2)',
          borderRadius: 'var(--radius-md)',
          border: '1px dashed var(--border)',
        }}>
          No sync runs yet. Trigger a sync from the Work Items tab.
        </div>
      )}

      {runs && runs.length > 0 && (
        <div style={{ display: 'grid', gap: 0 }}>
          {runs.map((run, i) => {
            const isExpanded = expanded.has(run.id);
            const ok = run.completedSuccessfully;
            const duration = run.finishedAt && run.startedAt
              ? Math.round((Date.parse(run.finishedAt) - Date.parse(run.startedAt)) / 1000)
              : null;

            return (
              <div key={run.id}>
                <button
                  type="button"
                  onClick={() => toggle(run.id)}
                  style={{
                    width: '100%',
                    display: 'grid',
                    gridTemplateColumns: '20px 1fr auto auto auto',
                    alignItems: 'center',
                    gap: 10,
                    padding: '10px 12px',
                    background: i % 2 === 0 ? 'transparent' : 'var(--bg-surface-2)',
                    border: 'none',
                    borderBottom: '1px solid var(--border)',
                    color: 'inherit',
                    font: 'inherit',
                    cursor: 'pointer',
                    textAlign: 'left',
                    fontSize: 12,
                    transition: 'background 100ms',
                  }}
                >
                  {/* Status indicator */}
                  <span style={{
                    width: 8, height: 8, borderRadius: 999,
                    background: ok ? 'var(--success)' : 'var(--danger)',
                    boxShadow: ok ? `0 0 4px var(--success-bg)` : `0 0 4px var(--danger-bg)`,
                  }} />

                  {/* Time */}
                  <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-secondary)' }}>
                    {relativeTime(run.startedAt)}
                  </span>

                  {/* Kind pill */}
                  <span style={{
                    padding: '2px 8px',
                    fontSize: 10,
                    fontWeight: 600,
                    textTransform: 'uppercase',
                    letterSpacing: 0.5,
                    borderRadius: 'var(--radius-sm)',
                    background: run.kind === 'full' ? 'var(--info-bg)' : run.kind === 'manual' ? 'var(--warning-bg)' : 'var(--bg-surface-2)',
                    color: run.kind === 'full' ? 'var(--info)' : run.kind === 'manual' ? 'var(--warning)' : 'var(--text-tertiary)',
                    border: `1px solid ${run.kind === 'full' ? 'var(--info-border)' : run.kind === 'manual' ? 'var(--warning-border)' : 'var(--border)'}`,
                  }}>
                    {run.kind}
                  </span>

                  {/* Counts */}
                  <span style={{ fontFamily: 'var(--font-mono)', fontSize: 11 }}>
                    <span style={{ color: 'var(--success)' }}>+{run.itemsAdded}</span>
                    <span style={{ color: 'var(--text-tertiary)' }}> / </span>
                    <span style={{ color: 'var(--info)' }}>~{run.itemsUpdated}</span>
                    {run.itemsConflicted > 0 && (
                      <>
                        <span style={{ color: 'var(--text-tertiary)' }}> / </span>
                        <span style={{ color: 'var(--warning)' }}>!{run.itemsConflicted}</span>
                      </>
                    )}
                  </span>

                  {/* Duration */}
                  <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)', minWidth: 40, textAlign: 'right' }}>
                    {duration !== null ? `${duration}s` : '—'}
                  </span>
                </button>

                {/* Expanded detail */}
                {isExpanded && (
                  <div style={{
                    padding: '12px 16px 12px 42px',
                    background: 'var(--bg-sunken)',
                    borderBottom: '1px solid var(--border)',
                    fontSize: 11.5,
                    animation: 'syncPanelFadeIn 120ms ease-out',
                  }}>
                    <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 16px', lineHeight: 1.6 }}>
                      <span style={detailLabelStyle}>Started</span>
                      <span style={detailValueStyle}>{run.startedAt}</span>
                      <span style={detailLabelStyle}>Finished</span>
                      <span style={detailValueStyle}>{run.finishedAt ?? '—'}</span>
                      <span style={detailLabelStyle}>Result</span>
                      <span style={{ ...detailValueStyle, color: ok ? 'var(--success)' : 'var(--danger)', fontWeight: 600 }}>
                        {ok ? 'Completed successfully' : 'Failed'}
                      </span>
                      <span style={detailLabelStyle}>Added</span>
                      <span style={detailValueStyle}>{run.itemsAdded}</span>
                      <span style={detailLabelStyle}>Updated</span>
                      <span style={detailValueStyle}>{run.itemsUpdated}</span>
                      <span style={detailLabelStyle}>Conflicted</span>
                      <span style={detailValueStyle}>{run.itemsConflicted}</span>
                    </div>

                    {(run.itemsAdded > 0 || run.itemsUpdated > 0) && (
                      <SyncRunItemList repoId={_repoId} runId={run.id} />
                    )}

                    {!ok && run.errorMessage && (
                      <div style={{
                        marginTop: 10,
                        padding: 10,
                        background: 'var(--danger-bg)',
                        border: '1px solid var(--danger-border)',
                        borderRadius: 'var(--radius-sm)',
                      }}>
                        <div style={{
                          fontWeight: 600,
                          color: 'var(--danger)',
                          fontSize: 11,
                          marginBottom: 4,
                          textTransform: 'uppercase',
                          letterSpacing: 0.3,
                        }}>
                          Error detail
                        </div>
                        <pre style={{
                          margin: 0,
                          fontFamily: 'var(--font-mono)',
                          fontSize: 10.5,
                          color: 'var(--text-secondary)',
                          whiteSpace: 'pre-wrap',
                          wordBreak: 'break-word',
                          lineHeight: 1.5,
                          maxHeight: 200,
                          overflow: 'auto',
                        }}>
                          {run.errorMessage}
                        </pre>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ── Sync Run Item List (Phase 25) ─────────────────────────────────────────

const ACTION_STYLE: Record<string, { color: string; label: string; icon: string }> = {
  added:   { color: 'var(--success)', label: 'Added',   icon: '+' },
  updated: { color: 'var(--info)',    label: 'Updated', icon: '~' },
  removed: { color: 'var(--danger)',  label: 'Removed', icon: '−' },
};

function SyncRunItemList({ repoId, runId }: { repoId: string; runId: string }) {
  const [items, setItems] = useState<SyncRunItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch(`/api/repos/${encodeURIComponent(repoId)}/sync-work-items/runs/${encodeURIComponent(runId)}/items`)
      .then((r) => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
      .then((data: SyncRunItem[]) => { if (!cancelled) setItems(data); })
      .catch((e: Error) => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [repoId, runId]);

  if (error) {
    return (
      <div style={{ marginTop: 10, fontSize: 10.5, color: 'var(--text-tertiary)' }}>
        Could not load details
      </div>
    );
  }
  if (items === null) {
    return (
      <div style={{ marginTop: 10, fontSize: 10.5, color: 'var(--text-tertiary)' }}>
        Loading items…
      </div>
    );
  }
  if (items.length === 0) return null;

  // Group by action
  const grouped = new Map<string, SyncRunItem[]>();
  for (const item of items) {
    const list = grouped.get(item.action) ?? [];
    list.push(item);
    grouped.set(item.action, list);
  }

  return (
    <div style={{ marginTop: 10 }}>
      <div style={{
        fontWeight: 600,
        fontSize: 10,
        textTransform: 'uppercase',
        letterSpacing: 0.4,
        color: 'var(--text-tertiary)',
        marginBottom: 6,
      }}>
        Changed items
      </div>
      {['added', 'updated', 'removed'].map((action) => {
        const group = grouped.get(action);
        if (!group || group.length === 0) return null;
        const style = ACTION_STYLE[action] ?? ACTION_STYLE.updated;
        return (
          <div key={action} style={{ marginBottom: 6 }}>
            <div style={{
              fontSize: 10,
              fontWeight: 600,
              color: style.color,
              textTransform: 'uppercase',
              letterSpacing: 0.3,
              marginBottom: 3,
            }}>
              {style.label} ({group.length})
            </div>
            {group.map((item) => (
              <div
                key={`${item.entityKind}-${item.entityId}`}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '3px 0',
                  fontSize: 11,
                  lineHeight: 1.4,
                }}
              >
                <span style={{
                  width: 14, height: 14,
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  borderRadius: 3,
                  fontSize: 10,
                  fontWeight: 700,
                  fontFamily: 'var(--font-mono)',
                  background: item.entityKind === 'pr' ? 'var(--accent-bg)' : 'var(--bg-surface-2)',
                  color: item.entityKind === 'pr' ? 'var(--accent)' : 'var(--text-tertiary)',
                  border: `1px solid ${item.entityKind === 'pr' ? 'var(--accent)' : 'var(--border)'}`,
                  flexShrink: 0,
                }}>
                  {item.entityKind === 'pr' ? 'P' : 'W'}
                </span>
                <span style={{ color: style.color, fontFamily: 'var(--font-mono)', fontSize: 10, flexShrink: 0 }}>
                  {style.icon}
                </span>
                <span style={{ color: 'var(--text-secondary)', fontFamily: 'var(--font-mono)', fontSize: 10, flexShrink: 0 }}>
                  {item.providerRef ? `#${item.providerRef}` : item.entityId}
                </span>
                <span style={{
                  color: 'var(--text-primary)',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}>
                  {item.title}
                </span>
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════
// ── Shared Primitives ─────────────────────────────────────────────────────
// ═══════════════════════════════════════════════════════════════════════════

function SectionHeader({ icon, title, subtitle }: { icon: string; title: string; subtitle: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{
        fontSize: 16,
        width: 28, height: 28,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        background: 'var(--accent-bg)',
        borderRadius: 'var(--radius-sm)',
        flexShrink: 0,
      }}>
        {icon}
      </span>
      <div>
        <div style={{
          fontFamily: 'var(--font-display)',
          fontWeight: 700,
          fontSize: 13.5,
          letterSpacing: -0.1,
          color: 'var(--text-primary)',
        }}>
          {title}
        </div>
        <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginTop: 1 }}>
          {subtitle}
        </div>
      </div>
    </div>
  );
}

function PanelField({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 5 }}>
      <span style={{
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: 0.2,
        textTransform: 'uppercase',
        color: 'var(--text-secondary)',
      }}>
        {label}
      </span>
      {children}
      {hint && (
        <span style={{ fontSize: 10.5, color: 'var(--text-tertiary)', lineHeight: 1.4 }}>
          {hint}
        </span>
      )}
    </label>
  );
}

// ── Shared Styles ───────────────────────────────────────────────────────

const fieldInputStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 11px',
  fontSize: 13,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const actionBtnStyle: React.CSSProperties = {
  padding: '8px 16px',
  fontSize: 12,
  fontWeight: 600,
  fontFamily: 'var(--font-body)',
  borderRadius: 'var(--radius-sm)',
  border: 'none',
  cursor: 'pointer',
  transition: 'all 120ms',
  letterSpacing: 0.1,
};

const detailLabelStyle: React.CSSProperties = {
  color: 'var(--text-tertiary)',
  fontWeight: 600,
  textTransform: 'uppercase' as const,
  fontSize: 10,
  letterSpacing: 0.3,
};

const detailValueStyle: React.CSSProperties = {
  fontFamily: 'var(--font-mono)',
  fontSize: 11,
  color: 'var(--text-secondary)',
};
