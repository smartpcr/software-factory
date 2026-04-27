import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import type { Workstream } from '@/types';
import { useStateStore } from '@/state/useStateStore';
import { useWorkstreamsStore, filterWorkstreams, type WorkstreamFilter } from '@/state/useWorkstreamsStore';
import { useBoardStore } from '@/state/useBoardStore';
import { useToastStore } from '@/state/useToastStore';
import { sampleState } from '@/sampleData';
import { StatsStrip } from '@/workstreams/StatsStrip';
import { WorkstreamCard } from '@/workstreams/WorkstreamCard';
import { NewWorkstreamCta } from '@/workstreams/NewWorkstreamCta';
import { NewWorkstreamModal } from '@/workstreams/NewWorkstreamModal';
import { Toaster } from '@/board/Toaster';
import { forgeApi } from '@/api/forgeApi';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { useQueueStore, useQueueChannel } from '@/realtime/useQueueStore';
import { AGENT_ALLOCATION } from '@/agentAllocation';

const FILTER_CHIPS: Array<{ key: WorkstreamFilter; label: string; pulse?: boolean }> = [
  { key: 'all',       label: 'All' },
  { key: 'active',    label: 'Active' },
  { key: 'merging',   label: 'Merging' },
  { key: 'conflicts', label: 'Conflicts', pulse: true },
  { key: 'idle',      label: 'Idle' },
];

export function WorkstreamsView() {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const wsStats = apiState?.wsStats ?? sampleState.wsStats;
  const allPrs = apiState?.prs ?? sampleState.prs;

  const workstreams = useWorkstreamsStore((s) => s.workstreams);
  const load = useWorkstreamsStore((s) => s.load);
  const filter = useWorkstreamsStore((s) => s.filter);
  const setFilter = useWorkstreamsStore((s) => s.setFilter);
  const focusedId = useWorkstreamsStore((s) => s.focusedId);
  const setFocused = useWorkstreamsStore((s) => s.setFocused);
  const syncOne = useWorkstreamsStore((s) => s.syncOne);
  const syncFromEpics = useWorkstreamsStore((s) => s.syncFromEpics);

  const allCards = useBoardStore((s) => s.cards);
  const allWorkItems = useWorkItemsStore((s) => s.items);
  // Phase 17.8 — subscribe to queue updates so the queued pill stays live.
  useQueueChannel({});
  const queueRows = useQueueStore((s) => s.rows);
  const refreshQueue = useQueueStore((s) => s.refresh);
  useEffect(() => { void refreshQueue(); }, [refreshQueue]);
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const repoFilter = searchParams.get('repo');

  const [modalOpen, setModalOpen] = useState(false);
  const [repoFilterLocal, setRepoFilterLocal] = useState('');
  const [teamFilter, setTeamFilter] = useState('');
  const [storyFilter, setStoryFilter] = useState('');

  useEffect(() => { void load(); }, [load]);

  // Derive dropdown options from workstream data.
  const repoOptions = useMemo(() => {
    const ids = new Set(workstreams.map((w) => w.repoId).filter(Boolean) as string[]);
    return [...ids].sort();
  }, [workstreams]);

  const teamOptions = useMemo(() => {
    const teams = new Set<string>();
    for (const ws of workstreams) {
      for (const agentId of ws.agents) {
        const entry = AGENT_ALLOCATION.find((a) => a.id === agentId);
        if (entry) teams.add(entry.team);
      }
    }
    return [...teams].sort();
  }, [workstreams]);

  const storyOptions = useMemo(() => {
    const epicIds = new Set(workstreams.map((w) => w.epic).filter(Boolean));
    return [...epicIds].map((id) => {
      const e = epics.find((ep) => ep.id === id);
      return { id, label: e ? `${e.id} — ${e.name}` : id };
    }).sort((a, b) => a.id.localeCompare(b.id));
  }, [workstreams, epics]);

  // Phase 3.10 — when the user clicks a card on the board with a `branch`,
  // the board store sets selectedId; if that card's branch matches a ws,
  // surface a soft highlight here.
  // Phase 8.11 — when ?repo=… is set, scope the visible workstreams to that
  // repo only (used by the products view's repo-row deep-link).
  const filtered = useMemo(() => {
    let next = filterWorkstreams(workstreams, filter);
    if (repoFilter) next = next.filter((w) => w.repoId === repoFilter);
    if (repoFilterLocal) next = next.filter((w) => w.repoId === repoFilterLocal);
    if (storyFilter) next = next.filter((w) => w.epic === storyFilter);
    if (teamFilter) {
      const teamAgentIds = new Set(AGENT_ALLOCATION.filter((a) => a.team === teamFilter).map((a) => a.id));
      next = next.filter((w) => w.agents.some((a) => teamAgentIds.has(a)));
    }
    return next;
  }, [workstreams, filter, repoFilter, repoFilterLocal, teamFilter, storyFilter]);
  const counts = useMemo(() => ({
    all:       workstreams.length,
    active:    workstreams.filter((w) => w.status === 'active' || w.status === 'review').length,
    merging:   workstreams.filter((w) => w.status === 'merging').length,
    conflicts: workstreams.filter((w) => w.conflicts > 0).length,
    idle:      workstreams.filter((w) => Math.abs(w.lastActive) >= 1).length,
  }), [workstreams]);

  function onOpen(ws: Workstream) {
    setFocused(ws.id);
    // Phase-3 cross-link: if the workstream has linked cards, route to /plan
    // and select the first one so the board's detail panel surfaces it.
    if (ws.cards.length > 0) {
      const first = ws.cards[0];
      useBoardStore.getState().setSelected(first);
      navigate('/plan');
      return;
    }
    useToastStore.getState().show(`Focused ${ws.name}`, 'success');
  }

  // Phase 6.4 — open a PR from this workstream via the configured provider.
  async function onOpenPr(ws: Workstream) {
    const title = window.prompt('PR title', `${ws.name}: ${ws.summary.split('.')[0]}`);
    if (!title) return;
    const provider = window.confirm('Use GitHub? (Cancel = ADO)') ? 'github' : 'ado';
    try {
      const pr = await forgeApi.openWorkstreamPr(ws.id, {
        title,
        body: `${ws.summary}\n\nLinked: ${ws.cards.join(', ')}`,
        provider,
        draft: false,
      });
      useToastStore.getState().show(`${pr.id} opened on ${provider}`, 'success');
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Open PR failed';
      useToastStore.getState().show(msg, 'danger');
    }
  }

  return (
    <section className="view active" id="view-workstreams">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">03</span>&nbsp;Workstreams</span>
            <span className="sep">/</span>
            <span>{counts.active} active · {counts.merging} merging · {workstreams.filter((w) => w.status === 'planning' || w.planned).length} planning</span>
            <span className="sep">/</span>
            <span><span className="live-dot" />{counts.conflicts} conflict{counts.conflicts === 1 ? '' : 's'}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Parallel workstreams
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-secondary" onClick={() => void syncFromEpics()}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M2 4 H 10 M 2 8 H 10" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
              <circle cx="6" cy="6" r="1.5" stroke="currentColor" strokeWidth="1.3" />
            </svg>
            Sync from epics
          </button>
          <button className="btn btn-primary" onClick={() => setModalOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New workstream
          </button>
        </div>
      </div>

      <div className="filter-bar" style={{ paddingBottom: 14 }}>
        <div className="filter-chips">
          {FILTER_CHIPS.map((chip) => (
            <button
              key={chip.key}
              className={`filter-chip${filter === chip.key ? ' active' : ''}`}
              onClick={() => setFilter(chip.key)}
            >
              {chip.pulse && <span className="pulse-dot" />}
              {chip.label} <span className="count">{counts[chip.key]}</span>
            </button>
          ))}
        </div>
        <select className="filter-select" value={repoFilterLocal} onChange={(e) => setRepoFilterLocal(e.target.value)}>
          <option value="">Repo: All</option>
          {repoOptions.map((r) => <option key={r} value={r}>{r}</option>)}
        </select>
        <select className="filter-select" value={teamFilter} onChange={(e) => setTeamFilter(e.target.value)}>
          <option value="">Team: All</option>
          {teamOptions.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <select className="filter-select" value={storyFilter} onChange={(e) => setStoryFilter(e.target.value)}>
          <option value="">Story: All</option>
          {storyOptions.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
        </select>
        <div className="filter-search">
          <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
            <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
            <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </svg>
          <input type="text" placeholder="Search workstreams…" />
        </div>
      </div>

      <StatsStrip stats={wsStats} />

      <div className="workstream-grid-wrap">
        {workstreams.length === 0 && (
          <div className="empty-state">
            <div className="title">No workstreams yet</div>
            <div className="sub">
              Spawn one from an epic on the board, or use “New workstream” above to create
              an empty one with its own branch + worktree.
            </div>
            <div className="actions">
              <button className="btn btn-primary" onClick={() => setModalOpen(true)}>New workstream</button>
              <button className="btn btn-secondary" onClick={() => void syncFromEpics()}>Sync from epics</button>
            </div>
          </div>
        )}
        <div className="workstream-grid" id="workstream-grid">
          {filtered.map((ws) => {
            // Phase 16.12 — count work items bound to this workstream that are
            // auto-dispatched and still in flight. The workstream's CardsJson
            // already lists the bound ids; we cross-walk into the WIT store.
            const liveItems = allWorkItems.filter(
              (w) => ws.cards.includes(w.id)
                  && w.autoDispatchedAt != null
                  && (w.state === 'proposed' || w.state === 'active'),
            );
            const liveCount = liveItems.length;
            const liveWorkItemId = liveItems[0]?.id ?? null;
            // Phase 17.8 — count queued rows for work items in this workstream.
            const queuedCount = Object.values(queueRows)
              .filter((r) => r.state === 'queued' && ws.cards.includes(r.workItemId))
              .length;
            return (
              <div
                key={ws.id}
                data-focused={focusedId === ws.id ? 'true' : undefined}
                style={focusedId === ws.id ? { outline: '2px solid var(--accent)', borderRadius: 'var(--radius-lg)' } : undefined}
              >
                <WorkstreamCard
                  ws={ws}
                  epic={epics.find((e) => e.id === ws.epic)}
                  prs={allPrs}
                  cards={allCards}
                  onOpen={onOpen}
                  onSync={(w) => void syncOne(w.id)}
                  onIde={(w) => useToastStore.getState().show(`Open ${w.name} in IDE — Phase R`, 'info')}
                  onOpenPr={(w) => void onOpenPr(w)}
                  liveCount={liveCount}
                  liveWorkItemId={liveWorkItemId}
                  onOpenLive={(wid) => navigate(`/work-items/${wid}/live`)}
                  queuedCount={queuedCount}
                  onOpenInbox={() => navigate('/inbox')}
                />
              </div>
            );
          })}
          <NewWorkstreamCta onClick={() => setModalOpen(true)} />
        </div>
      </div>

      <NewWorkstreamModal open={modalOpen} onClose={() => setModalOpen(false)} />
      <Toaster />
    </section>
  );
}


