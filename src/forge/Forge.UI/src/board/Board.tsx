import { useEffect, useMemo, useState, useCallback } from 'react';
import type { Card, Epic, ProductStory } from '@/types';
import { useStateStore } from '@/state/useStateStore';
import { useBoardStore, filterCards } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { sampleState } from '@/sampleData';
import { SwimlaneCell } from './SwimlaneCell';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import { StoryPicker } from './StoryPicker';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

const COLLAPSED_KEY = 'forge.board.collapsed';

function loadCollapsed(): Set<string> {
  try {
    const raw = window.localStorage.getItem(COLLAPSED_KEY);
    return raw ? new Set(JSON.parse(raw) as string[]) : new Set();
  } catch { return new Set(); }
}

function saveCollapsed(set: Set<string>) {
  window.localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...set]));
}

export function Board() {
  const apiState = useStateStore((s) => s.state);
  const legacyEpics = apiState?.epics ?? sampleState.epics;
  const columns = apiState?.columns ?? sampleState.columns;
  const groupBy = useBoardStore((s) => s.groupBy);
  const setSelectedStory = useBoardStore((s) => s.setSelectedStory);
  const activeProductId = useProductsStore((s) => s.activeProductId);

  // Product-scoped stories — primary grouping source for 'story' mode.
  const [productStories, setProductStories] = useState<ProductStory[]>([]);
  useEffect(() => {
    if (!activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setProductStories(s); })
      .catch(() => { /* best effort */ });
    return () => { cancelled = true; };
  }, [activeProductId]);

  // Convert product stories to Epic shape for the swimlane engine.
  const storyEpics: Epic[] = useMemo(() => productStories.map((s) => ({
    id: s.id, name: s.name, status: s.status as Epic['status'],
    progress: 0, crew: [], cost: 0, color: s.id.toLowerCase(),
  })), [productStories]);

  // Pick the right epic list based on groupBy mode.
  const epics = groupBy === 'story' ? storyEpics : legacyEpics;

  const cards = useBoardStore((s) => s.cards);
  const activeFilter = useBoardStore((s) => s.activeFilter);
  const epicFilter = useBoardStore((s) => s.epicFilter);
  const agentFilter = useBoardStore((s) => s.agentFilter);
  const priorityFilter = useBoardStore((s) => s.priorityFilter);
  const storyFilter = useBoardStore((s) => s.storyFilter);
  const searchTerm = useBoardStore((s) => s.searchTerm);
  const filtered = useMemo(
    () => filterCards(cards, { activeFilter, epicFilter, agentFilter, priorityFilter, storyFilter, searchTerm }),
    [cards, activeFilter, epicFilter, agentFilter, priorityFilter, storyFilter, searchTerm],
  );

  // Compute swimlane groups based on groupBy.
  const swimlanes = useMemo(() => computeSwimlanes(filtered, epics, groupBy), [filtered, epics, groupBy]);

  // Story picker state for assigning orphan cards to a story.
  const [storyPickerOpen, setStoryPickerOpen] = useState(false);

  // Swimlane collapse state — persisted per-lane in localStorage.
  const [collapsed, setCollapsed] = useState<Set<string>>(loadCollapsed);
  const toggleCollapse = useCallback((laneId: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(laneId)) next.delete(laneId);
      else next.add(laneId);
      saveCollapsed(next);
      return next;
    });
  }, []);

  const assignOrphanCards = useCallback(async (storyId: string) => {
    if (!storyId) return;
    const knownIds = new Set(epics.map((e) => e.id));
    const orphans = filtered.filter((c) => !c.epic || !knownIds.has(c.epic));
    for (const card of orphans) {
      try {
        await forgeApi.patchCard(card.id, { epic: storyId });
      } catch {
        // best-effort
      }
    }
    useToastStore.getState().show(`Assigned ${orphans.length} card${orphans.length === 1 ? '' : 's'} to story`, 'success');
    useStateStore.getState().load();
  }, [filtered, epics]);

  // Column-level stats are computed across ALL filtered cards, not per-cell.
  const colStats = useMemo(() => {
    return columns.map((c) => {
      const colCards = filtered.filter((card) => card.col === c.id);
      const totalPts = colCards.reduce((s, x) => s + x.pts, 0);
      const aiCount = colCards.filter((x) => x.agent && x.agent.role !== 'human').length;
      const humanCount = colCards.filter((x) => x.agent && x.agent.role === 'human').length;
      const overWip = c.wipLimit != null && colCards.length > c.wipLimit;
      return { col: c, count: colCards.length, totalPts, aiCount, humanCount, overWip };
    });
  }, [columns, filtered]);

  const totalActive = swimlanes.length;
  const totalItems = filtered.length;
  const groupLabel = groupBy === 'story' ? 'Story' : groupBy === 'epic' ? 'Epic'
    : groupBy === 'phase' ? 'Phase' : groupBy === 'agent' ? 'Agent' : 'Priority';

  return (
    <div className="board" id="board">
      <div className="col-head swimlane-corner">
        <div className="col-name" style={{ fontSize: 9, color: 'var(--text-tertiary)', marginBottom: 4 }}>Group by</div>
        <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)' }}>{groupLabel}</div>
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)', marginTop: 4 }}>
          {totalActive} active · {totalItems} items
        </div>
      </div>

      {colStats.map((cs) => (
        <div key={cs.col.id} className="col-head">
          <div className="col-head-row">
            <span className="col-name"><span className={`col-marker ${cs.col.id}`} />{cs.col.name}</span>
            <span className="col-stats">
              <span className={`col-wip ${cs.overWip ? 'over-limit' : ''}`}>
                {cs.count}{cs.col.wipLimit != null ? ` / ${cs.col.wipLimit}` : ''}
              </span>
              <span className="col-pts">{cs.totalPts}pt</span>
            </span>
          </div>
          <div className="col-foot-row">
            {cs.aiCount > 0 && <span className="ai"><span className="ai-icon" />{cs.aiCount} AI</span>}
            {cs.humanCount > 0 && <span className="ai"><span className="human-icon" />{cs.humanCount} human</span>}
            {cs.aiCount === 0 && cs.humanCount === 0 && <span style={{ opacity: 0.4 }}>—</span>}
          </div>
        </div>
      ))}

      {swimlanes.map((lane) => {
        const isNoGroup = lane.id === '__none__';
        const isCollapsed = collapsed.has(lane.id);
        const laneCards = columns.flatMap((c) => lane.cardFilter(filtered, c.id));
        const laneCardCount = laneCards.length;
        const lanePts = laneCards.reduce((s, x) => s + x.pts, 0);
        return (
          <FragmentRow key={lane.id}>
            <div
              className={`swimlane-head${isCollapsed ? ' swimlane-collapsed' : ''}`}
              style={{
                ...(isNoGroup ? { position: 'relative' } : {}),
                ...(isCollapsed ? { gridColumn: '1 / -1', minHeight: 'auto' } : {}),
                ...(groupBy === 'story' && !isNoGroup && !isCollapsed ? { cursor: 'pointer' } : {}),
              }}
              onDoubleClick={isNoGroup && !isCollapsed ? () => setStoryPickerOpen(true) : undefined}
              onClick={
                isCollapsed
                  ? () => toggleCollapse(lane.id)
                  : (groupBy === 'story' && !isNoGroup
                      ? () => setSelectedStory(lane.id)
                      : undefined)
              }
              title={
                isCollapsed
                  ? 'Click to expand'
                  : isNoGroup
                    ? 'Double-click to assign cards to a story'
                    : groupBy === 'story'
                      ? 'Click to open story detail'
                      : undefined
              }
            >
              <div className="swimlane-epic">
                <span className="swimlane-epic-id">{lane.id}</span>
                <button
                  className={`swimlane-collapse${isCollapsed ? ' collapsed' : ''}`}
                  title={isCollapsed ? 'Expand' : 'Collapse'}
                  onClick={(e) => { e.stopPropagation(); toggleCollapse(lane.id); }}
                >
                  <svg width="8" height="8" viewBox="0 0 8 8">
                    <path
                      d={isCollapsed ? 'M3 1.5 L5 4 L3 6.5' : 'M1.5 3 L4 5 L6.5 3'}
                      stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round"
                    />
                  </svg>
                </button>
              </div>
              {isCollapsed ? (
                <div className="swimlane-collapsed-summary">
                  <span className="swimlane-name" style={{ fontSize: 12 }}>{lane.name}</span>
                  <span className="swimlane-collapsed-stats">
                    {laneCardCount} item{laneCardCount !== 1 ? 's' : ''} · {lanePts}pt
                  </span>
                </div>
              ) : (
                <>
                  <div className="swimlane-name">{lane.name}</div>
                  {lane.progress != null && (
                    <div className="swimlane-progress">
                      <div className="swimlane-progress-bar"><div className={`swimlane-progress-fill ${lane.status ?? 'planning'}`} style={{ width: `${lane.progress}%` }} /></div>
                      <div className="swimlane-progress-row"><span>{lane.status ?? ''}</span><span>{lane.progress}%</span></div>
                    </div>
                  )}
                  <div className="swimlane-foot">
                    <div className="swimlane-crew">
                      {(lane.crew ?? []).map((id) => {
                        const role = crewRoleFor(id);
                        const av = avatarFor(id);
                        const tip = agentTooltip(id, undefined, role);
                        return av ? (
                          <img
                            key={id}
                            src={av}
                            alt={id}
                            className={`agent-chip-avatar ${role}`}
                            style={{ marginLeft: -3, borderColor: 'var(--bg-sunken)', width: 17, height: 17 }}
                            title={tip}
                          />
                        ) : (
                          <span
                            key={id}
                            className={`agent-chip ${role}`}
                            style={{ marginLeft: -3, borderColor: 'var(--bg-sunken)', width: 17, height: 17, fontSize: 7 }}
                            title={tip}
                          >
                            {id}
                          </span>
                        );
                      })}
                    </div>
                    {lane.cost != null && <span className="swimlane-cost">${lane.cost.toFixed(2)}</span>}
                  </div>
                  {isNoGroup && (
                    <StoryPicker
                      open={storyPickerOpen}
                      currentStory=""
                      onSelect={(epicId) => void assignOrphanCards(epicId)}
                      onClose={() => setStoryPickerOpen(false)}
                    />
                  )}
                </>
              )}
            </div>
            {!isCollapsed && columns.map((c) => {
              const cellCards = lane.cardFilter(filtered, c.id);
              return <SwimlaneCell key={c.id} epic={lane.asEpic} col={c.id} cards={cellCards} />;
            })}
          </FragmentRow>
        );
      })}
    </div>
  );
}

// React requires a wrapping component for keyed siblings; <Fragment key=...>
// works but doesn't accept className. The board grid expects the head + cells
// as direct children of #board, so we render them via a no-DOM Fragment.
function FragmentRow({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}

function crewRoleFor(id: string) {
  return id.startsWith('A') ? 'arch'
    : id.startsWith('E') ? 'eng'
    : id.startsWith('R') ? 'rev'
    : id.startsWith('Q') ? 'qa'
    : 'human';
}

// ---- swimlane grouping engine ------------------------------------------

interface Swimlane {
  id: string;
  name: string;
  status?: string;
  progress?: number;
  crew?: string[];
  cost?: number;
  /** Epic object passed to SwimlaneCell for DnD compatibility. */
  asEpic: Epic;
  /** Returns cards for a given column within this swimlane. */
  cardFilter: (cards: Card[], colId: string) => Card[];
}

const NONE_EPIC: Epic = {
  id: '__none__', name: 'Ungrouped', status: 'planning',
  progress: 0, crew: [], cost: 0, color: 'none',
};

function computeSwimlanes(cards: Card[], epics: Epic[], groupBy: string): Swimlane[] {
  switch (groupBy) {
    case 'story':
    case 'epic':
      return computeEpicSwimlanes(cards, epics);
    case 'phase':
      return computePhaseSwimlanes(cards);
    case 'agent':
      return computeAgentSwimlanes(cards);
    case 'priority':
      return computePrioritySwimlanes(cards);
    default:
      return computeEpicSwimlanes(cards, epics);
  }
}

function computeEpicSwimlanes(cards: Card[], epics: Epic[]): Swimlane[] {
  const epicIds = new Set(epics.map((e) => e.id));
  const lanes: Swimlane[] = epics.map((e) => ({
    id: e.id,
    name: e.name,
    status: e.status,
    progress: e.progress,
    crew: e.crew,
    cost: e.cost,
    asEpic: e,
    cardFilter: (all, colId) => all.filter((c) => c.epic === e.id && c.col === colId),
  }));
  const hasOrphans = cards.some((c) => !c.epic || !epicIds.has(c.epic));
  if (hasOrphans) {
    lanes.push({
      id: '__none__',
      name: 'No story',
      asEpic: NONE_EPIC,
      cardFilter: (all, colId) => all.filter((c) => (!c.epic || !epicIds.has(c.epic)) && c.col === colId),
    });
  }
  return lanes;
}

function computePhaseSwimlanes(cards: Card[]): Swimlane[] {
  // Group by phase-related tags: kind:phase, kind:stage, kind:step.
  const phaseKeys = ['kind:phase', 'kind:stage', 'kind:step'];
  const groups = new Map<string, { label: string }>();
  groups.set('phase', { label: 'Phase' });
  groups.set('stage', { label: 'Stage' });
  groups.set('step', { label: 'Step' });
  groups.set('other', { label: 'Other' });

  function resolvePhase(c: Card): string {
    for (const pk of phaseKeys) {
      if (c.tags.includes(pk)) return pk.replace('kind:', '');
    }
    if (c.tags.includes('task:plan')) return 'phase';
    return 'other';
  }

  // Only include groups that have cards.
  const seen = new Set(cards.map(resolvePhase));
  const lanes: Swimlane[] = [];
  for (const [key, { label }] of groups) {
    if (!seen.has(key)) continue;
    const epic: Epic = { id: key, name: label, status: 'planning', progress: 0, crew: [], cost: 0, color: key };
    lanes.push({
      id: key,
      name: label,
      asEpic: epic,
      cardFilter: (all, colId) => all.filter((c) => resolvePhase(c) === key && c.col === colId),
    });
  }
  return lanes.length > 0 ? lanes : [{ id: '__none__', name: 'All cards', asEpic: NONE_EPIC, cardFilter: (all, colId) => all.filter((c) => c.col === colId) }];
}

function computeAgentSwimlanes(cards: Card[]): Swimlane[] {
  const agentIds = new Set<string>();
  let hasUnassigned = false;
  for (const c of cards) {
    // Cards whose agent has no resolvable id (provider-side data gap)
    // collapse into the unassigned lane rather than producing an empty
    // swimlane keyed on the empty string.
    if (c.agent && c.agent.id) agentIds.add(c.agent.id);
    else hasUnassigned = true;
  }
  const lanes: Swimlane[] = [...agentIds].sort().map((aid) => {
    const sample = cards.find((c) => c.agent?.id === aid);
    const epic: Epic = { id: aid, name: sample?.agent?.name ?? aid, status: 'active', progress: 0, crew: [aid], cost: 0, color: aid };
    return {
      id: aid,
      name: sample?.agent?.name ?? aid,
      crew: [aid],
      asEpic: epic,
      cardFilter: (all, colId) => all.filter((c) => c.agent?.id === aid && c.col === colId),
    };
  });
  if (hasUnassigned) {
    lanes.push({
      id: '__none__',
      name: 'Unassigned',
      asEpic: NONE_EPIC,
      cardFilter: (all, colId) => all.filter((c) => !c.agent && c.col === colId),
    });
  }
  return lanes;
}

function computePrioritySwimlanes(cards: Card[]): Swimlane[] {
  const order: string[] = ['high', 'med', 'low'];
  const seen = new Set<string>(cards.map((c) => c.priority));
  return order.filter((p) => seen.has(p)).map((p) => {
    const label = p === 'high' ? '🔴 High' : p === 'med' ? '🟡 Medium' : '🟢 Low';
    const epic: Epic = { id: p, name: label, status: 'active', progress: 0, crew: [], cost: 0, color: p };
    return {
      id: p,
      name: label,
      asEpic: epic,
      cardFilter: (all, colId) => all.filter((c) => c.priority === p && c.col === colId),
    };
  });
}
