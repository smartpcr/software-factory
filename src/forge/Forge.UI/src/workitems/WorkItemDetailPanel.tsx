import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { WorkItem, WorkItemAssignee, WorkItemPriority, WorkItemState, WorkItemType, AssignedAgent } from './workItemTypes';
import { typeLabel, WORK_ITEM_STATES, WORK_ITEM_TYPES } from './workItemTypes';
import { useWitStore } from './useWitStore';
import { useWorkItemsStore } from './useWorkItemsStore';
import { TypeMark } from './TypeMark';
import { StatePill } from './StatePill';
import { ExecBadge } from './ExecBadge';
import { RepoIcon } from '@/products/RepoIcon';
import { SAMPLE_ITERATIONS, SAMPLE_REPOS } from './sampleWorkItems';
import { useProductsStore } from '@/state/useProductsStore';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import { CardModal } from '@/board/CardModal';
import { StoryPicker } from '@/board/StoryPicker';
import type { AgentDetail, Crew, ProductAreaPath, Repo } from '@/types';

interface Props {
  item: WorkItem | null;
}

// Phase 12 — right rail. Inline-edit shape for the implementation step is
// scaffolded with read-only displays today (sample data, no API). The layout
// is intentionally publication-style: a manuscript page with type-set
// metadata and a markdown preview body.
export function WorkItemDetailPanel({ item }: Props) {
  const focus = useWitStore((s) => s.focus);
  const expanded = useWitStore((s) => s.panelExpanded);
  const togglePanelExpanded = useWitStore((s) => s.togglePanelExpanded);
  const allItems = useWorkItemsStore((s) => s.items);
  const patch = useWorkItemsStore((s) => s.patch);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const navigate = useNavigate();

  // Local-edit buffers so we can submit on blur/Enter without re-rendering on every keystroke.
  const [titleDraft, setTitleDraft] = useState(item?.title ?? '');
  const [editingTitle, setEditingTitle] = useState(false);
  useEffect(() => { setTitleDraft(item?.title ?? ''); setEditingTitle(false); }, [item?.id, item?.title]);

  // Phase 14 — "Create card & dispatch" reuses the existing CardModal
  // pre-seeded from this WI (skipping the createWorkItem step).
  const [dispatchModalOpen, setDispatchModalOpen] = useState(false);
  const [resyncing, setResyncing] = useState(false);
  const [witStoryPickerOpen, setWitStoryPickerOpen] = useState(false);
  const reload = useWorkItemsStore((s) => s.load);

  // Product-scoped stories for the story name display + picker.
  const [productStories, setProductStories] = useState<import('@/types').ProductStory[]>([]);
  useEffect(() => {
    if (!activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setProductStories(s); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [activeProductId]);
  async function resyncFromProvider() {
    if (!item) return;
    setResyncing(true);
    try {
      // Per-WI re-sync — bypasses the WIQL change filter and the
      // revision-skip so an unchanged remote still re-applies (which is
      // what we want when the local schema evolves, e.g. after the
      // ADO mapper started composing description + repro + system info).
      await forgeApi.resyncWorkItem(item.id);
      await reload();
      const { useToastStore } = await import('@/state/useToastStore');
      useToastStore.getState().show(
        `Re-synced ${item.id} from ${item.provider}`,
        'success',
      );
    } catch (err) {
      const { useToastStore } = await import('@/state/useToastStore');
      const msg = err instanceof ForgeApiError
        ? `${err.status}: ${err.problem?.detail ?? err.problem?.title ?? err.message}`
        : err instanceof Error ? err.message : 'Re-sync failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setResyncing(false);
    }
  }

  // Repos scoped to the active product (Q5 — a repo belongs to exactly one
  // product, so showing repos from other products is a UX bug).
  const productRepos = useMemo<Repo[]>(
    () => activeProductId ? (reposByProduct[activeProductId] ?? []) : [],
    [activeProductId, reposByProduct],
  );

  // Area paths for the active product. Loaded once per product change.
  const [areaPaths, setAreaPaths] = useState<ProductAreaPath[]>([]);
  useEffect(() => {
    if (!activeProductId) { setAreaPaths([]); return; }
    let cancelled = false;
    forgeApi.listAreaPaths(activeProductId)
      .then((rs) => { if (!cancelled) setAreaPaths(rs); })
      .catch(() => { if (!cancelled) setAreaPaths([]); });
    return () => { cancelled = true; };
  }, [activeProductId]);

  // Agents + crews for the assignee picker. Lazy — only loaded when the
  // picker opens (the operator may never reassign).
  const [agents, setAgents] = useState<AgentDetail[]>([]);
  const [crews, setCrews] = useState<Crew[]>([]);
  const [agentsLoaded, setAgentsLoaded] = useState(false);
  async function ensureAgentsLoaded() {
    if (agentsLoaded) return;
    try {
      const [rs, cs] = await Promise.all([
        forgeApi.listAgents(),
        forgeApi.listCrews().catch(() => [] as Crew[]),
      ]);
      setAgents(rs);
      setCrews(cs);
    } catch { /* swallow */ }
    setAgentsLoaded(true);
  }

  if (!item) {
    return (
      <aside className="wit-detail wit-detail-empty">
        <div className="wit-detail-empty-mark">§</div>
        <div className="wit-detail-empty-title">Select an item</div>
        <div className="wit-detail-empty-sub">
          Click any work item to read, edit, or re-parent. Multi-select with shift-click on Board / List for bulk operations.
        </div>
      </aside>
    );
  }

  const repo = productRepos.find((r) => r.id === item.repoId);
  const repoLite = repo ?? SAMPLE_REPOS.find((r) => r.id === item.repoId);
  const breadcrumb = useMemo(() => ancestry(item, allItems), [item, allItems]);
  const children = useMemo(() => allItems.filter((c) => c.parentId === item.id), [item, allItems]);

  function commitTitle() {
    setEditingTitle(false);
    if (titleDraft.trim() && titleDraft !== item!.title) {
      void patch(item!.id, { title: titleDraft.trim() });
    } else {
      setTitleDraft(item!.title);
    }
  }

  function setState(s: WorkItemState) {
    if (s !== item!.state) void patch(item!.id, { state: s });
  }
  function setPriority(p: WorkItemPriority) {
    if (p !== item!.priority) void patch(item!.id, { priority: p });
  }
  function setPoints(p: number) {
    if (p !== item!.points && p >= 0) void patch(item!.id, { points: p });
  }
  function setIteration(ref: string) {
    if (ref !== item!.iterationRef) void patch(item!.id, { iterationRef: ref });
  }
  function setAreaPath(area: string) {
    if (area !== item!.areaPath) void patch(item!.id, { areaPath: area });
  }
  function setRepo(id: string) {
    if (id === (item!.repoId ?? '')) return;
    void patch(item!.id, id ? { repoId: id } : { clearRepoId: true });
  }
  function setType(t: WorkItemType) {
    if (t !== item!.type) void patch(item!.id, { type: t });
  }
  function setAssignee(next: WorkItemAssignee | null) {
    if (next === null) {
      if (item!.assignedTo) void patch(item!.id, { clearAssignedTo: true });
      return;
    }
    if (item!.assignedTo?.id === next.id && item!.assignedTo.kind === next.kind) return;
    void patch(item!.id, { assignedTo: next });
  }

  return (
    <aside className={`wit-detail${expanded ? ' wit-detail-expanded' : ''}`}>
      <header className="wit-detail-head">
        <div className="wit-detail-meta-line">
          <TypeMark type={item.type} size={20} />
          <span className="wit-detail-id">{item.id}</span>
          {item.providerRef !== null && (
            (() => {
              const url = buildProviderUrl(item, repo ?? undefined);
              const label = item.provider === 'ado' ? `WI #${item.providerRef}` : `#${item.providerRef}`;
              return url ? (
                <a
                  className="wit-detail-provider-ref"
                  href={url}
                  target="_blank"
                  rel="noopener noreferrer"
                  title={`Open in ${item.provider === 'ado' ? 'Azure DevOps' : 'GitHub'}`}
                  style={{ textDecoration: 'none' }}
                >
                  {label}
                  <svg width="10" height="10" viewBox="0 0 12 12" fill="none" aria-hidden style={{ marginLeft: 3, verticalAlign: 'middle', opacity: 0.6 }}>
                    <path d="M4.5 2H2.5A1.5 1.5 0 0 0 1 3.5v6A1.5 1.5 0 0 0 2.5 11h6A1.5 1.5 0 0 0 10 9.5V7.5M7 1h4v4M11 1 5.5 6.5" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </a>
              ) : (
                <span className="wit-detail-provider-ref">{label}</span>
              );
            })()
          )}
          {repoLite && <RepoIcon kind={repoLite.kind} size={14} title={repoLite.displayName} />}
          <div className="wit-detail-head-actions">
            {item.provider !== 'local' && item.repoId && (
              <button
                type="button"
                className="wit-detail-icon-btn"
                onClick={() => void resyncFromProvider()}
                disabled={resyncing}
                aria-label={`Re-sync from ${item.provider.toUpperCase()}`}
                title={`Re-sync this item's title / description / state from ${item.provider === 'ado' ? 'Azure DevOps' : 'GitHub'}.`}
              >
                <svg
                  width="13" height="13" viewBox="0 0 12 12" fill="none" aria-hidden
                  style={resyncing ? { animation: 'spin 1.2s linear infinite' } : undefined}
                >
                  <path d="M2 6 a 4 4 0 0 1 7 -2.5 M 10 6 a 4 4 0 0 1 -7 2.5 M 9 1.5 V 3.5 H 7 M 3 10.5 V 8.5 H 5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
            )}
            <button
              type="button"
              className="wit-detail-icon-btn"
              onClick={() => setDispatchModalOpen(true)}
              aria-label="Create card and dispatch to crew"
              title="Create card & dispatch to AI crew"
              style={{ color: 'var(--accent)', fontWeight: 600 }}
            >
              ✦
            </button>
            <button
              type="button"
              className="wit-detail-icon-btn"
              onClick={togglePanelExpanded}
              aria-label={expanded ? 'Collapse panel' : 'Expand panel'}
              title={expanded ? 'Collapse panel (Esc)' : 'Expand to full screen'}
            >
              {expanded ? (
                <svg width="13" height="13" viewBox="0 0 14 14" fill="none" aria-hidden>
                  <path d="M3 3 H 6 V 6 M 3 3 L 7 7 M 11 11 H 8 V 8 M 11 11 L 7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              ) : (
                <svg width="13" height="13" viewBox="0 0 14 14" fill="none" aria-hidden>
                  <path d="M3 3 H 6 M 3 3 V 6 M 3 3 L 7 7 M 11 11 H 8 M 11 11 V 8 M 11 11 L 7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              )}
            </button>
            <button
              type="button"
              className="wit-detail-icon-btn wit-detail-close"
              onClick={() => focus(null)}
              aria-label="Close panel"
              title="Close panel (Esc)"
            >
              ×
            </button>
          </div>
        </div>
        {editingTitle ? (
          <input
            type="text"
            className="wit-detail-title-input"
            value={titleDraft}
            autoFocus
            onChange={(e) => setTitleDraft(e.target.value)}
            onBlur={commitTitle}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitTitle();
              if (e.key === 'Escape') { setTitleDraft(item.title); setEditingTitle(false); }
            }}
          />
        ) : (
          <h2
            className="wit-detail-title wit-detail-title-editable"
            onClick={() => setEditingTitle(true)}
            title="Click to edit"
          >
            {item.title}
          </h2>
        )}
        <div className="wit-detail-eyebrow-row">
          <StateSelect value={item.state} onChange={setState} />
          <div className="wit-detail-eyebrow-meta">
            <span className="lbl">Execution</span>
            <ExecBadge status={item.executionStatus} trialCount={item.trialCount} />
          </div>
          <IterationSelect value={item.iterationRef} repoId={item.repoId} onChange={setIteration} />
          <PointsStepper value={item.points} onChange={setPoints} />
          <PrioritySelect value={item.priority} onChange={setPriority} />
        </div>
      </header>

      {breadcrumb.length > 0 && (
        <nav className="wit-detail-breadcrumb" aria-label="Hierarchy">
          {breadcrumb.map((p, i) => (
            <span key={p.id} className="wit-detail-breadcrumb-seg">
              <button type="button" onClick={() => focus(p.id)}>
                <TypeMark type={p.type} size={14} muted />
                <span className="seg-id">{p.id}</span>
                <span className="seg-title">{p.title}</span>
              </button>
              {i < breadcrumb.length - 1 && <span className="wit-detail-breadcrumb-sep">▸</span>}
            </span>
          ))}
        </nav>
      )}

      <div style={{ display: 'flex', gap: 8, padding: '8px 0' }}>
        <button
          className="btn btn-primary"
          style={{ flex: 1, fontSize: 12 }}
          onClick={() => navigate(`/work-items/${encodeURIComponent(item.id)}/live`)}
          title="Open live execution view"
        >
          ▶ Live Execution
        </button>
        <button
          className="btn btn-secondary"
          style={{ fontSize: 12 }}
          onClick={() => setDispatchModalOpen(true)}
          title="Create card & dispatch to AI crew"
        >
          Dispatch
        </button>
      </div>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Assignee</div>
        <AssigneePicker
          value={item.assignedTo}
          agents={agents}
          crews={crews}
          onOpen={ensureAgentsLoaded}
          onChange={setAssignee}
        />
      </section>

      {/* Phase 22 — runtime agent assignment. Dispatch to team/crew to start
           the message queue; lead delegates to an agent via the queue system. */}
      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Agent Assignment</div>
        <AgentAssignmentSection
          value={item.assignedAgent ?? null}
          crews={crews}
          onDispatch={setAssignee}
          onClear={() => {
            void forgeApi.clearAgentAssignment(item.id, 'cleared from detail panel')
              .then(() => reload())
              .catch(() => {});
          }}
          onOpen={ensureAgentsLoaded}
        />
      </section>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Story</div>
        <div style={{ position: 'relative' }}>
          <button
            type="button"
            onClick={() => setWitStoryPickerOpen((v) => !v)}
            style={{
              background: 'transparent',
              border: '1px solid var(--border)',
              borderRadius: 'var(--radius-sm)',
              padding: '5px 10px',
              color: item.featureRef ? 'var(--text-primary)' : 'var(--text-tertiary)',
              cursor: 'pointer',
              fontSize: 12,
              fontStyle: item.featureRef ? 'normal' : 'italic',
              width: '100%',
              textAlign: 'left',
            }}
          >
            {item.featureRef
              ? (() => { const s = productStories.find((st) => st.id === item.featureRef); return s ? s.name : item.featureRef; })()
              : 'No story — click to assign'}
          </button>
          <StoryPicker
            open={witStoryPickerOpen}
            currentStory={item.featureRef ?? ''}
            onSelect={(epicId) => {
              void (async () => {
                // 1. Update the work item's featureRef.
                await patch(item.id, { featureRef: epicId || null });
                // 2. Ensure a board card exists so the item appears on
                //    Plan & Design. If one already exists, update its epic;
                //    otherwise create one.
                if (epicId) {
                  try {
                    await forgeApi.patchCard(item.id, { epic: epicId });
                  } catch {
                    // Card doesn't exist yet — create it.
                    try {
                      await forgeApi.createCard({
                        id: item.id,
                        epic: epicId,
                        col: 'backlog',
                        type: item.type === 'bug' ? 'bug' : item.type === 'task' ? 'task' : 'story',
                        title: item.title,
                        tags: item.tags,
                        pts: item.points,
                        priority: item.priority,
                        agent: null,
                        cost: 0,
                      });
                    } catch {
                      // best-effort
                    }
                  }
                }
              })();
              setWitStoryPickerOpen(false);
            }}
            onClose={() => setWitStoryPickerOpen(false)}
          />
        </div>
      </section>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Description</div>
        <MarkdownPreview md={item.descriptionMd} />
      </section>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">
          Tags
          <span className="wit-detail-section-count">{item.tags.length}</span>
        </div>
        <div className="wit-detail-tags">
          {item.tags.length === 0 && <span className="muted">— none</span>}
          {item.tags.map((t) => {
            // Phase 23.8 — agent-ready upstream tag gets an orange pill so
            // operators can see at a glance which items the auto-team-assign
            // touched. Match case-insensitively to align with the resolver.
            const isAgentReady = t.toLowerCase() === 'agent-ready';
            if (isAgentReady) {
              return (
                <span
                  key={t}
                  className="wit-tag"
                  data-testid="agent-ready-pill"
                  title="agent-ready — auto-team-assigned by sync (Phase 23.8 / Q23.8)"
                  style={{
                    background: 'color-mix(in srgb, rgb(234, 88, 12) 14%, transparent)',
                    color: 'rgb(234, 88, 12)',
                    border: '1px solid rgb(234, 88, 12)',
                    fontWeight: 600,
                    letterSpacing: 0.4,
                    textTransform: 'uppercase',
                    fontSize: 10,
                  }}
                >
                  agent-ready
                </span>
              );
            }
            return <span key={t} className="wit-tag">{t}</span>;
          })}
        </div>
      </section>

      {children.length > 0 && (
        <section className="wit-detail-section">
          <div className="wit-detail-section-title">
            Children
            <span className="wit-detail-section-count">{children.length}</span>
          </div>
          <ul className="wit-detail-children">
            {children.map((c) => (
              <li key={c.id}>
                <button type="button" onClick={() => focus(c.id)}>
                  <TypeMark type={c.type} size={14} />
                  <span className="cid">{c.id}</span>
                  <span className="ctitle">{c.title}</span>
                  <StatePill state={c.state} size="sm" />
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Provenance</div>
        <dl className="wit-detail-provenance">
          <dt>Area</dt>
          <dd><AreaPathSelect value={item.areaPath} options={areaPaths} onChange={setAreaPath} /></dd>
          <dt>Repo</dt>
          <dd><RepoSelect value={item.repoId ?? ''} repos={productRepos} onChange={setRepo} /></dd>
          <dt>Created</dt><dd>{fmt(item.createdAt)}</dd>
          <dt>Updated</dt><dd>{fmt(item.updatedAt)}</dd>
          <dt>Type</dt><dd><TypeSelect value={item.type} onChange={setType} /></dd>
        </dl>
      </section>

      <footer className="wit-detail-foot" style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <button
          className="btn btn-secondary"
          onClick={async () => {
            try {
              const queue = await forgeApi.listQueue();
              const queueEntry = queue.find((q: { workItemId: string }) => q.workItemId === item.id);
              if (queueEntry) {
                await forgeApi.cancelQueue(queueEntry.id, 're-queue from detail panel');
              }
              // Resolve crew from: queue row → assignedTo kind=crew → assignedTo kind=agent's crew.
              let crewId: string | undefined = (queueEntry as { routedToCrewId?: string } | undefined)?.routedToCrewId ?? undefined;
              if (!crewId && item.assignedTo?.kind === 'crew') crewId = item.assignedTo.id;
              if (!crewId && item.assignedTo?.kind === 'agent') {
                // Look up which crew this agent belongs to.
                try {
                  const crews: Array<{ id: string; members?: Array<{ agentId: string }> }> = await forgeApi.listCrews();
                  crewId = crews.find(c => c.members?.some(m => m.agentId === item.assignedTo?.id))?.id;
                } catch { /* best effort */ }
              }
              if (crewId) {
                await forgeApi.requeueWorkItem(crewId, item.id);
              } else {
                throw new Error('No crew found to re-queue to. Assign the item to a crew first.');
              }
              const { useToastStore } = await import('@/state/useToastStore');
              useToastStore.getState().show('Re-queued', 'success');
            } catch (e) {
              const { useToastStore } = await import('@/state/useToastStore');
              useToastStore.getState().show(e instanceof Error ? e.message : 'Failed', 'danger');
            }
          }}
          title="Cancel current queue entry and re-enqueue"
        >
          ↻ Re-queue
        </button>
        <button
          className="btn btn-secondary"
          style={{ color: 'var(--danger, #ef4444)' }}
          onClick={async () => {
            try {
              const queue = await forgeApi.listQueue();
              const entry = queue.find((q: { workItemId: string }) => q.workItemId === item.id);
              if (entry) {
                await forgeApi.cancelQueue(entry.id, 'unassign from detail panel');
              }
              await patch(item.id, { clearAssignedTo: true });
              const { useToastStore } = await import('@/state/useToastStore');
              useToastStore.getState().show('Unassigned — ready to re-dispatch', 'success');
            } catch (e) {
              const { useToastStore } = await import('@/state/useToastStore');
              useToastStore.getState().show(e instanceof Error ? e.message : 'Failed', 'danger');
            }
          }}
          title="Cancel queue entry and clear agent assignment"
        >
          ✕ Unassign
        </button>
      </footer>

      <CardModal
        open={dispatchModalOpen}
        onClose={() => setDispatchModalOpen(false)}
        fromWorkItem={item}
      />
    </aside>
  );
}

// ---- inline-edit sub-components ----------------------------------------

function StateSelect({ value, onChange }: { value: WorkItemState; onChange: (s: WorkItemState) => void }) {
  return (
    <label className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">State</span>
      <select
        className="wit-detail-state-select"
        value={value}
        onChange={(e) => onChange(e.target.value as WorkItemState)}
        aria-label="Work item state"
      >
        {WORK_ITEM_STATES.map((s) => (
          <option key={s} value={s}>{s}</option>
        ))}
      </select>
    </label>
  );
}

function PointsStepper({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(String(value));

  function commitEdit() {
    setEditing(false);
    const n = parseInt(draft, 10);
    if (!Number.isNaN(n) && n >= 0 && n !== value) onChange(n);
    else setDraft(String(value));
  }

  // Keep draft in sync when value changes externally.
  useEffect(() => { if (!editing) setDraft(String(value)); }, [value, editing]);

  return (
    <span className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">Points</span>
      <span className="wit-detail-points-stepper">
        <button
          type="button"
          aria-label="Decrement points"
          disabled={value <= 0}
          onClick={() => onChange(Math.max(0, value - 1))}
        >−</button>
        {editing ? (
          <input
            type="number"
            min={0}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={commitEdit}
            onKeyDown={(e) => { if (e.key === 'Enter') commitEdit(); if (e.key === 'Escape') { setEditing(false); setDraft(String(value)); } }}
            autoFocus
            style={{
              width: 36, textAlign: 'center', fontSize: 12,
              fontFamily: 'var(--font-mono)', fontWeight: 600,
              background: 'var(--bg-surface)', color: 'var(--text-primary)',
              border: '1px solid var(--accent)', borderRadius: 2,
              outline: 'none', padding: '0 2px',
              MozAppearance: 'textfield',
            }}
          />
        ) : (
          <span
            className="val"
            onClick={() => { setDraft(String(value)); setEditing(true); }}
            title="Click to edit points"
            style={{ cursor: 'text' }}
          >
            {value || '—'}
          </span>
        )}
        <button
          type="button"
          aria-label="Increment points"
          onClick={() => onChange(value + 1)}
        >+</button>
      </span>
    </span>
  );
}

function IterationSelect({ value, repoId, onChange }: {
  value: string;
  repoId: string | null;
  onChange: (next: string) => void;
}) {
  // Scope to iterations whose repo matches the work item, else show all.
  const opts = useMemo(() => {
    const all = SAMPLE_ITERATIONS;
    if (!repoId) return all;
    const scoped = all.filter((it) => it.ref.startsWith(`${repoId}:`));
    return scoped.length > 0 ? scoped : all;
  }, [repoId]);
  return (
    <label className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">Iteration</span>
      <select
        className="wit-detail-state-select"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label="Iteration"
      >
        <option value="">— none —</option>
        {opts.map((it) => (
          <option key={it.ref} value={it.ref}>{it.label}</option>
        ))}
        {value && !opts.some((o) => o.ref === value) && (
          <option value={value}>{value} (out of scope)</option>
        )}
      </select>
    </label>
  );
}

function AreaPathSelect({ value, options, onChange }: {
  value: string;
  options: ProductAreaPath[];
  onChange: (next: string) => void;
}) {
  // Free-form fallback when the active product hasn't imported any nodes yet —
  // keep the operator able to type a value rather than locking the field.
  if (options.length === 0) {
    return (
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="No area paths imported · type one"
        style={{
          background: 'transparent', border: '1px solid var(--border-strong)',
          borderRadius: 4, padding: '2px 6px', color: 'inherit', font: 'inherit', width: '100%',
        }}
      />
    );
  }
  const known = options.some((o) => o.path === value);
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Area path"
      style={selectStyle}
    >
      <option value="">— none —</option>
      {options.map((o) => (
        <option key={o.id} value={o.path}>{o.path}</option>
      ))}
      {value && !known && <option value={value}>{value} (custom)</option>}
    </select>
  );
}

function RepoSelect({ value, repos, onChange }: {
  value: string;
  repos: Repo[];
  onChange: (id: string) => void;
}) {
  // Defensive — if the product hasn't loaded yet, fall through to the static
  // sample list so the dropdown isn't empty during boot.
  const opts = repos.length > 0 ? repos : SAMPLE_REPOS;
  const known = opts.some((r) => r.id === value);
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      aria-label="Repo"
      style={selectStyle}
    >
      <option value="">— unattached —</option>
      {opts.map((r) => (
        <option key={r.id} value={r.id}>
          {r.displayName} · {r.kind === 'ado' ? 'Azure DevOps' : 'GitHub'}
        </option>
      ))}
      {value && !known && <option value={value}>{value} (other product)</option>}
    </select>
  );
}

function TypeSelect({ value, onChange }: { value: WorkItemType; onChange: (t: WorkItemType) => void }) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value as WorkItemType)}
      aria-label="Type"
      style={selectStyle}
      title="Type changes auto-flip the track (epic/feature/task/bug → dev; phase/stage/step → design)."
    >
      {WORK_ITEM_TYPES.map((t) => (
        <option key={t} value={t}>{typeLabel(t)}</option>
      ))}
    </select>
  );
}

const selectStyle: React.CSSProperties = {
  background: 'transparent', border: '1px solid var(--border-strong)',
  borderRadius: 4, padding: '2px 6px', color: 'inherit', font: 'inherit',
};

function AssigneePicker({ value, agents, crews, onOpen, onChange }: {
  value: WorkItemAssignee | null;
  agents: AgentDetail[];
  crews: Crew[];
  onOpen: () => void;
  onChange: (a: WorkItemAssignee | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const filteredCrews = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return crews;
    return crews.filter((c) =>
      c.id.toLowerCase().includes(q)
      || c.name.toLowerCase().includes(q)
      || (c.lead ?? '').toLowerCase().includes(q),
    );
  }, [crews, query]);

  const filteredAgents = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return agents;
    return agents.filter((a) =>
      a.id.toLowerCase().includes(q)
      || a.name.toLowerCase().includes(q)
      || a.role.toLowerCase().includes(q)
      || (a.crewId ?? '').toLowerCase().includes(q),
    );
  }, [agents, query]);

  function pickCrew(c: Crew) {
    // The crew's name is the displayName; initials use the first two
    // capitalized letters of the name (e.g. "Engineering" → "EN").
    const initials = (c.name.match(/[A-Z]/g)?.join('') ?? c.name.slice(0, 2)).slice(0, 2).toUpperCase();
    onChange({
      kind: 'crew',
      id: c.id,
      displayName: c.name,
      initials,
    });
    setOpen(false); setQuery('');
  }

  // Phase 17 — assign to a line-of-business team. The server enqueues
  // a `task_queue` row with state='awaiting-ack' and the lead has 10s to
  // acknowledge before auto-ack. Visible in /inbox.
  function pickTeam(name: string) {
    onChange({
      kind: 'team',
      id: name,
      displayName: name,
      initials: name.slice(0, 2).toUpperCase(),
    });
    setOpen(false); setQuery('');
  }

  function pickAgent(a: AgentDetail) {
    onChange({
      kind: 'agent',
      id: a.id,
      displayName: a.name || a.id,
      initials: a.id.slice(0, 2).toUpperCase(),
    });
    setOpen(false); setQuery('');
  }

  function openPicker() {
    setOpen(true);
    void onOpen();
  }

  return (
    <div style={{ position: 'relative' }}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => (open ? setOpen(false) : openPicker())}
        className="wit-detail-assignee"
        style={{
          display: 'flex', alignItems: 'center', gap: 8,
          width: '100%', padding: 6, border: '1px solid var(--border)',
          borderRadius: 4, background: 'transparent', cursor: 'pointer',
          color: 'inherit', textAlign: 'left',
        }}
        title="Click to reassign"
      >
        {value ? (
          <>
            {value.kind === 'agent' && avatarFor(value.id, value.displayName) ? (
              <img
                src={avatarFor(value.id, value.displayName)!}
                alt={value.displayName}
                style={{ width: 26, height: 26, borderRadius: 999 }}
              />
            ) : value.kind === 'crew' ? (
              <TeamAvatar
                lead={crews.find((c) => c.id === value.id)?.lead ?? null}
                initials={value.initials}
                size={26}
              />
            ) : value.kind === 'team' ? (
              <span style={{
                width: 26, height: 26, borderRadius: 6,
                display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                background: 'color-mix(in srgb, var(--warning) 15%, transparent)',
                border: '1px solid var(--warning)',
                color: 'var(--warning)',
                fontFamily: 'var(--font-mono, monospace)', fontSize: 11, fontWeight: 600,
              }}>{value.initials}</span>
            ) : (
              <span className="wit-detail-avatar" style={{ width: 26, height: 26 }}>{value.initials}</span>
            )}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className="wit-detail-assignee-name">
                {value.displayName}
                {value.kind === 'crew' && (
                  <span style={{
                    marginLeft: 6, fontSize: 9.5, padding: '1px 6px', borderRadius: 999,
                    border: '1px solid var(--accent)', color: 'var(--accent)',
                    fontFamily: 'var(--font-mono, monospace)', fontWeight: 400,
                  }}>crew</span>
                )}
                {value.kind === 'team' && (
                  <span style={{
                    marginLeft: 6, fontSize: 9.5, padding: '1px 6px', borderRadius: 999,
                    border: '1px solid var(--warning)', color: 'var(--warning)',
                    fontFamily: 'var(--font-mono, monospace)', fontWeight: 400,
                  }}>queue</span>
                )}
              </div>
              <div className="wit-detail-assignee-kind">{value.kind} · {value.id}</div>
            </div>
            <span aria-hidden style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>▾</span>
          </>
        ) : (
          <>
            <span className="muted" style={{ flex: 1 }}>— unassigned (click to pick team or agent)</span>
            <span aria-hidden style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>▾</span>
          </>
        )}
      </button>
      {open && (
        <div
          ref={popoverRef}
          style={{
            position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 30,
            maxHeight: 360, overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)', borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)',
          }}
        >
          <div style={{ position: 'sticky', top: 0, background: 'inherit', padding: 6, borderBottom: '1px solid var(--border)' }}>
            <input
              type="text"
              autoFocus
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search agents by id, name, role, crew…"
              style={{
                width: '100%', padding: '4px 8px', fontSize: 11.5,
                background: 'var(--bg-base, transparent)', color: 'var(--text-primary)',
                border: '1px solid var(--border-strong)', borderRadius: 4,
              }}
            />
          </div>
          <button type="button" onClick={() => { onChange(null); setOpen(false); }} style={pickerRowStyle(false)}>
            <span style={{ width: 26, height: 26, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', color: 'var(--text-tertiary)' }}>—</span>
            <span style={{ flex: 1, fontStyle: 'italic', color: 'var(--text-secondary)' }}>Unassigned</span>
          </button>

          {/* Phase 17 — line-of-business teams. Picking one enqueues the
              work item, kicks off the 10s lead-ack SLO, and routes through
              the queue. Visible in /inbox. */}
          {(() => {
            const q = query.trim().toLowerCase();
            const teams = TEAMS.filter((t) => !q || t.toLowerCase().includes(q));
            if (teams.length === 0) return null;
            return (
              <>
                <div style={pickerSectionHeaderStyle}>Teams · routes via queue</div>
                {teams.map((t) => {
                  const selected = value?.kind === 'team' && value.id === t;
                  return (
                    <button
                      key={`team:${t}`}
                      type="button"
                      onClick={() => pickTeam(t)}
                      style={pickerRowStyle(selected)}
                      title={`Route to the ${t} team. Lead has 10s to acknowledge before auto-ack — track in /inbox.`}
                    >
                      <span style={{
                        width: 26, height: 26, borderRadius: 6,
                        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                        background: 'color-mix(in srgb, var(--warning) 15%, transparent)',
                        border: '1px solid var(--warning)',
                        color: 'var(--warning)',
                        fontFamily: 'var(--font-mono, monospace)', fontSize: 11, fontWeight: 600,
                      }}>{t.slice(0, 2).toUpperCase()}</span>
                      <span style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ fontWeight: 600, fontSize: 12.5 }}>
                          {t}
                          <span style={{
                            marginLeft: 6, fontSize: 9, padding: '1px 5px', borderRadius: 999,
                            border: '1px solid var(--warning)', color: 'var(--warning)',
                            fontFamily: 'var(--font-mono, monospace)',
                          }}>queue</span>
                        </div>
                        <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                          lead acks → routes to the matching crew
                        </div>
                      </span>
                    </button>
                  );
                })}
              </>
            );
          })()}

          {filteredCrews.length > 0 && (
            <>
              <div style={pickerSectionHeaderStyle}>Crews · direct (skip queue)</div>
              {filteredCrews.map((c) => {
                const initials = (c.name.match(/[A-Z]/g)?.join('') ?? c.name.slice(0, 2)).slice(0, 2).toUpperCase();
                const selected = value?.kind === 'crew' && value.id === c.id;
                return (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => pickCrew(c)}
                    style={pickerRowStyle(selected)}
                    title={c.summary || c.name}
                  >
                    <TeamAvatar lead={c.lead} initials={initials} size={26} />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ fontWeight: 600, fontSize: 12.5 }}>
                        {c.name}
                        <span style={{
                          marginLeft: 6, fontSize: 9, padding: '1px 5px', borderRadius: 999,
                          border: '1px solid var(--accent)', color: 'var(--accent)',
                          fontFamily: 'var(--font-mono, monospace)',
                        }}>team</span>
                      </div>
                      <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                        {c.lead ? `lead ${c.lead}` : 'no lead'}
                        {c.summary ? ` · ${c.summary}` : ''}
                      </div>
                    </span>
                  </button>
                );
              })}
            </>
          )}

          {filteredAgents.length > 0 && (
            <div style={pickerSectionHeaderStyle}>Individuals · {filteredAgents.length}</div>
          )}
          {filteredAgents.length === 0 && filteredCrews.length === 0 ? (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No matches.</div>
          ) : filteredAgents.map((a) => {
            const url = avatarFor(a.id, a.name);
            const tip = agentTooltip(a.id, a.name, a.role, a.soulPath);
            const selected = value?.kind === 'agent' && value.id === a.id;
            return (
              <button
                key={a.id}
                type="button"
                onClick={() => pickAgent(a)}
                style={pickerRowStyle(selected)}
                title={tip}
              >
                {url ? (
                  <img src={url} alt={a.name} style={{ width: 26, height: 26, borderRadius: 999, objectFit: 'cover' }} />
                ) : (
                  <span style={{
                    width: 26, height: 26, borderRadius: 999, display: 'inline-flex',
                    alignItems: 'center', justifyContent: 'center',
                    background: 'var(--bg-sunken, #2a2a2a)', color: 'var(--text-secondary)',
                    fontFamily: 'var(--font-mono, monospace)', fontSize: 11,
                  }}>{a.id.slice(0, 2)}</span>
                )}
                <span style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontWeight: 600, fontSize: 12.5 }}>
                    {a.name}
                    <span style={{ color: 'var(--text-tertiary)', marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10 }}>{a.id}</span>
                  </div>
                  <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                    {a.role}{a.crewId ? ` · ${a.crewId}` : ''}
                  </div>
                </span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

/**
 * Phase 22 — interactive agent assignment section. Shows the current runtime
 * agent assignment and lets the operator dispatch to a team or crew (which
 * enqueues via the task queue and lets the lead delegate).
 */
function AgentAssignmentSection({ value, crews, onDispatch, onClear, onOpen }: {
  value: AssignedAgent | null;
  crews: Crew[];
  onDispatch: (target: WorkItemAssignee) => void;
  onClear: () => void;
  onOpen: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const filteredCrews = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return crews;
    return crews.filter((c) =>
      c.id.toLowerCase().includes(q)
      || c.name.toLowerCase().includes(q)
      || (c.lead ?? '').toLowerCase().includes(q),
    );
  }, [crews, query]);

  function pickTeam(name: string) {
    onDispatch({
      kind: 'team',
      id: name,
      displayName: name,
      initials: name.slice(0, 2).toUpperCase(),
    });
    setOpen(false); setQuery('');
  }

  function pickCrew(c: Crew) {
    const initials = (c.name.match(/[A-Z]/g)?.join('') ?? c.name.slice(0, 2)).slice(0, 2).toUpperCase();
    onDispatch({
      kind: 'crew',
      id: c.id,
      displayName: c.name,
      initials,
    });
    setOpen(false); setQuery('');
  }

  function openPicker() {
    setOpen(true);
    void onOpen();
  }

  const agentList = value?.agents?.join(', ') || null;
  const ts = value?.assignedAt
    ? new Date(value.assignedAt).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
    : null;

  return (
    <div style={{ position: 'relative' }}>
      {/* Current assignment display */}
      {value ? (
        <div style={{
          padding: '6px 8px',
          border: '1px solid var(--border)',
          borderRadius: 4,
          fontSize: 12,
          display: 'flex',
          flexDirection: 'column',
          gap: 4,
          marginBottom: 6,
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            {value.agents?.map((agentId) => {
              const url = avatarFor(agentId, null);
              return url ? (
                <img key={agentId} src={url} alt={agentId}
                  style={{ width: 22, height: 22, borderRadius: 999 }}
                  title={agentId}
                />
              ) : (
                <span key={agentId} style={{
                  width: 22, height: 22, borderRadius: 999,
                  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                  background: 'var(--bg-sunken, #2a2a2a)', color: 'var(--text-secondary)',
                  fontFamily: 'var(--font-mono, monospace)', fontSize: 10,
                }}>{agentId.slice(0, 2)}</span>
              );
            })}
            <span style={{ fontWeight: 600, flex: 1 }}>{agentList}</span>
            {value.role && (
              <span style={{
                fontSize: 9.5, padding: '1px 6px', borderRadius: 999,
                border: '1px solid var(--accent)', color: 'var(--accent)',
                fontFamily: 'var(--font-mono, monospace)', fontWeight: 400,
              }}>{value.role}</span>
            )}
            <button
              type="button"
              onClick={onClear}
              style={{
                background: 'transparent', border: 'none', color: 'var(--danger, #e55)',
                cursor: 'pointer', fontSize: 11, fontFamily: 'var(--font-mono, monospace)',
              }}
              title="Clear agent assignment"
            >✕</button>
          </div>
          <div style={{ fontSize: 10, color: 'var(--text-tertiary)', display: 'flex', gap: 8 }}>
            {value.team && <span>team: {value.team}</span>}
            {value.crewId && <span>crew: {value.crewId}</span>}
            {value.assignedBy && <span>by: {value.assignedBy}</span>}
            {ts && <span>at: {ts}</span>}
          </div>
        </div>
      ) : null}

      {/* Dispatch button */}
      <button
        ref={triggerRef}
        type="button"
        onClick={() => (open ? setOpen(false) : openPicker())}
        className="wit-detail-assignee"
        style={{
          display: 'flex', alignItems: 'center', gap: 8,
          width: '100%', padding: 6, border: '1px solid var(--border)',
          borderRadius: 4, background: 'transparent', cursor: 'pointer',
          color: 'inherit', textAlign: 'left',
        }}
        title="Dispatch to team or crew — starts the message queue"
      >
        <span style={{
          width: 22, height: 22, borderRadius: 6,
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          background: 'color-mix(in srgb, var(--warning) 15%, transparent)',
          border: '1px solid var(--warning)',
          color: 'var(--warning)',
          fontSize: 12,
        }}>⚡</span>
        <span style={{ flex: 1, fontSize: 12, color: 'var(--text-secondary)' }}>
          {value ? 'Re-dispatch to team / crew…' : 'Dispatch to team / crew…'}
        </span>
        <span aria-hidden style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>▾</span>
      </button>

      {/* Picker popover */}
      {open && (
        <div
          ref={popoverRef}
          style={{
            position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 30,
            maxHeight: 320, overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)', borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)',
          }}
        >
          <div style={{ position: 'sticky', top: 0, background: 'inherit', padding: 6, borderBottom: '1px solid var(--border)' }}>
            <input
              type="text"
              autoFocus
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search teams or crews…"
              style={{
                width: '100%', padding: '4px 8px', fontSize: 11.5,
                background: 'var(--bg-base, transparent)', color: 'var(--text-primary)',
                border: '1px solid var(--border-strong)', borderRadius: 4,
              }}
            />
          </div>

          {/* Teams */}
          {(() => {
            const q = query.trim().toLowerCase();
            const teams = TEAMS.filter((t) => !q || t.toLowerCase().includes(q));
            if (teams.length === 0) return null;
            return (
              <>
                <div style={pickerSectionHeaderStyle}>Teams · routes via queue</div>
                {teams.map((t) => (
                  <button
                    key={`team:${t}`}
                    type="button"
                    onClick={() => pickTeam(t)}
                    style={pickerRowStyle(false)}
                    title={`Dispatch to ${t} team. Lead has 10s to ack → routes to crew.`}
                  >
                    <span style={{
                      width: 26, height: 26, borderRadius: 6,
                      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                      background: 'color-mix(in srgb, var(--warning) 15%, transparent)',
                      border: '1px solid var(--warning)',
                      color: 'var(--warning)',
                      fontFamily: 'var(--font-mono, monospace)', fontSize: 11, fontWeight: 600,
                    }}>{t.slice(0, 2).toUpperCase()}</span>
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ fontWeight: 600, fontSize: 12.5 }}>
                        {t}
                        <span style={{
                          marginLeft: 6, fontSize: 9, padding: '1px 5px', borderRadius: 999,
                          border: '1px solid var(--warning)', color: 'var(--warning)',
                          fontFamily: 'var(--font-mono, monospace)',
                        }}>queue</span>
                      </div>
                      <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                        lead acks → routes to the matching crew
                      </div>
                    </span>
                  </button>
                ))}
              </>
            );
          })()}

          {/* Crews */}
          {filteredCrews.length > 0 && (
            <>
              <div style={pickerSectionHeaderStyle}>Crews · direct (skip queue)</div>
              {filteredCrews.map((c) => {
                const initials = (c.name.match(/[A-Z]/g)?.join('') ?? c.name.slice(0, 2)).slice(0, 2).toUpperCase();
                return (
                  <button
                    key={c.id}
                    type="button"
                    onClick={() => pickCrew(c)}
                    style={pickerRowStyle(false)}
                    title={c.summary || c.name}
                  >
                    <TeamAvatar lead={c.lead} initials={initials} size={26} />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ fontWeight: 600, fontSize: 12.5 }}>
                        {c.name}
                        <span style={{
                          marginLeft: 6, fontSize: 9, padding: '1px 5px', borderRadius: 999,
                          border: '1px solid var(--accent)', color: 'var(--accent)',
                          fontFamily: 'var(--font-mono, monospace)',
                        }}>crew</span>
                      </div>
                      <div style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
                        {c.lead ? `lead ${c.lead}` : 'no lead'}
                        {c.summary ? ` · ${c.summary}` : ''}
                      </div>
                    </span>
                  </button>
                );
              })}
            </>
          )}

          {filteredCrews.length === 0 && query.trim() && (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No matches.</div>
          )}
        </div>
      )}
    </div>
  );
}

function pickerRowStyle(selected: boolean): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 8,
    width: '100%', padding: '6px 8px',
    background: selected ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
    border: 'none', borderBottom: '1px solid var(--border)',
    color: 'inherit', cursor: 'pointer', textAlign: 'left',
  };
}

/** Phase 17 — line-of-business teams that can own a work item. */
const TEAMS = ['Architecture', 'Engineering', 'Review', 'QA'] as const;

const pickerSectionHeaderStyle: React.CSSProperties = {
  padding: '8px 10px 4px',
  fontFamily: 'var(--font-mono, monospace)',
  fontSize: 9.5,
  color: 'var(--text-tertiary)',
  textTransform: 'uppercase',
  letterSpacing: 0.12,
  borderBottom: '1px solid var(--border)',
};

/**
 * Team avatar — uses the lead's portrait when one exists, with a small
 * accent-bordered "team" badge in the corner so a team is still visually
 * distinct from picking the lead as an individual. Falls back to
 * accent-bordered initials when the lead has no portrait or the crew
 * has no lead.
 */
function TeamAvatar({ lead, initials, size = 26 }: {
  lead: string | null | undefined;
  initials: string;
  size?: number;
}) {
  const url = lead ? avatarFor(lead, null) : null;
  if (url) {
    return (
      <span style={{ position: 'relative', display: 'inline-block', width: size, height: size }}>
        <img
          src={url}
          alt={`team ${initials}`}
          style={{ width: size, height: size, borderRadius: 6, objectFit: 'cover', border: '1px solid var(--accent)' }}
        />
        <span
          aria-hidden
          title="team"
          style={{
            position: 'absolute', bottom: -3, right: -3,
            width: 12, height: 12, borderRadius: 999,
            background: 'var(--accent)', color: 'var(--bg-base, white)',
            fontSize: 8, fontWeight: 700,
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            border: '1px solid var(--bg-base, var(--bg-elevated))',
            fontFamily: 'var(--font-mono, monospace)',
          }}
        >T</span>
      </span>
    );
  }
  return (
    <span style={{
      width: size, height: size, borderRadius: 6, display: 'inline-flex',
      alignItems: 'center', justifyContent: 'center',
      background: 'color-mix(in srgb, var(--accent) 18%, transparent)',
      color: 'var(--accent)', fontWeight: 700, fontSize: size * 0.42,
      border: '1px solid var(--accent)',
    }}>{initials}</span>
  );
}

function PrioritySelect({ value, onChange }: { value: WorkItemPriority; onChange: (p: WorkItemPriority) => void }) {
  const options: WorkItemPriority[] = ['low', 'med', 'high'];
  return (
    <span className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">Priority</span>
      <span className="wit-detail-pri-chips" role="radiogroup" aria-label="Priority">
        {options.map((p) => (
          <button
            key={p}
            type="button"
            role="radio"
            aria-checked={value === p}
            className={`wit-detail-pri-chip pri-${p}${value === p ? ' active' : ''}`}
            onClick={() => onChange(p)}
          >
            {p}
          </button>
        ))}
      </span>
    </span>
  );
}

function ancestry(item: WorkItem, all: WorkItem[]): WorkItem[] {
  const out: WorkItem[] = [];
  const seen = new Set<string>();
  let cur: WorkItem | null = item.parentId
    ? all.find((w) => w.id === item.parentId) ?? null
    : null;
  while (cur && !seen.has(cur.id)) {
    seen.add(cur.id);
    out.unshift(cur);
    cur = cur.parentId ? all.find((w) => w.id === cur!.parentId) ?? null : null;
  }
  return out;
}

// Build a deep-link URL to the original work item in its provider.
// ADO: https://dev.azure.com/{org}/{project}/_workitems/edit/{id}
// GitHub: https://github.com/{owner}/{repo}/issues/{id}
function buildProviderUrl(item: WorkItem, repo?: Repo): string | null {
  if (item.providerRef == null) return null;
  if (item.provider === 'ado') {
    if (repo?.adoOrg && repo.adoProject) {
      const orgUrl = repo.adoOrg.startsWith('http')
        ? repo.adoOrg
        : `https://dev.azure.com/${repo.adoOrg}`;
      return `${orgUrl}/${repo.adoProject}/_workitems/edit/${item.providerRef}`;
    }
    // Fallback: try to derive from remoteUrl (e.g. https://dev.azure.com/{org}/{project}/_git/{repo})
    if (repo?.remoteUrl) {
      const m = repo.remoteUrl.match(/^(https:\/\/dev\.azure\.com\/[^/]+\/[^/]+)/);
      if (m) return `${m[1]}/_workitems/edit/${item.providerRef}`;
    }
    return null;
  }
  if (item.provider === 'github') {
    if (repo?.ghOwner && repo.ghRepo) {
      return `https://github.com/${repo.ghOwner}/${repo.ghRepo}/issues/${item.providerRef}`;
    }
    return null;
  }
  return null;
}

function fmt(iso: string): string {
  const d = new Date(iso);
  return `${d.toISOString().slice(0, 10)} ${d.toISOString().slice(11, 16)}`;
}

// Tiny inline markdown preview — handles headings, bold, italic, inline
// code, and paragraphs. Enough for the read-only mockup; the real editor
// arrives with Phase 12.7.
function MarkdownPreview({ md }: { md: string }) {
  if (!md.trim()) return <div className="muted">— no description</div>;
  // ADO descriptions are HTML (contain <div>, <br>, etc.).
  // Render HTML content directly; only run the markdown pipeline for plain text.
  const isHtml = /<\/?[a-z][\s\S]*>/i.test(md);
  if (isHtml) {
    return <div className="wit-detail-md" dangerouslySetInnerHTML={{ __html: md }} />;
  }
  const html = renderMd(md);
  return <div className="wit-detail-md" dangerouslySetInnerHTML={{ __html: html }} />;
}

function renderMd(md: string): string {
  // Escape first.
  const esc = (s: string) => s
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
  const lines = esc(md).split('\n');
  const out: string[] = [];
  let para: string[] = [];
  const flush = () => { if (para.length) { out.push(`<p>${inline(para.join(' '))}</p>`); para = []; } };
  for (const line of lines) {
    const t = line.trim();
    if (!t) { flush(); continue; }
    const h = /^(#{1,3})\s+(.+)$/.exec(t);
    if (h) {
      flush();
      const lvl = h[1].length;
      out.push(`<h${lvl}>${inline(h[2])}</h${lvl}>`);
      continue;
    }
    if (t.startsWith('- ')) {
      flush();
      out.push(`<ul><li>${inline(t.slice(2))}</li></ul>`);
      continue;
    }
    para.push(t);
  }
  flush();
  // Coalesce adjacent <ul>s — naive but enough for the mockup.
  return out.join('\n').replace(/<\/ul>\n<ul>/g, '');
}

function inline(s: string): string {
  return s
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '<em>$1</em>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/~~([^~]+)~~/g, '<del>$1</del>');
}
