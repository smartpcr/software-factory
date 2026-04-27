import { useMemo } from 'react';
import { useStateStore } from '@/state/useStateStore';
import { useBoardStore, filterCards } from '@/state/useBoardStore';
import { sampleState } from '@/sampleData';
import { SwimlaneCell } from './SwimlaneCell';
import { avatarFor, agentTooltip } from '@/agentAvatar';

// Verbatim port of the prototype's renderBoard (lines 3310-3382).
// Layout uses CSS grid (defined in board.css); we only emit the same
// children in the same order with the same classNames.
export function Board() {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const columns = apiState?.columns ?? sampleState.columns;

  const cards = useBoardStore((s) => s.cards);
  const activeFilter = useBoardStore((s) => s.activeFilter);
  const filtered = useMemo(() => filterCards(cards, activeFilter), [cards, activeFilter]);

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

  const totalActive = epics.length;
  const totalItems = filtered.length;

  return (
    <div className="board" id="board">
      <div className="col-head swimlane-corner">
        <div className="col-name" style={{ fontSize: 9, color: 'var(--text-tertiary)', marginBottom: 4 }}>Group by</div>
        <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-primary)' }}>Epic</div>
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

      {epics.map((epic) => {
        const crewRoleFor = (id: string) =>
          id.startsWith('A') ? 'arch'
            : id.startsWith('E') ? 'eng'
            : id.startsWith('R') ? 'rev'
            : id.startsWith('Q') ? 'qa'
            : 'human';
        return (
          <FragmentRow key={epic.id}>
            <div className="swimlane-head">
              <div className="swimlane-epic">
                <span className="swimlane-epic-id">{epic.id}</span>
                <button className="swimlane-collapse" title="Collapse">
                  <svg width="8" height="8" viewBox="0 0 8 8"><path d="M1.5 3 L4 5 L6.5 3" stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round" /></svg>
                </button>
              </div>
              <div className="swimlane-name">{epic.name}</div>
              <div className="swimlane-progress">
                <div className="swimlane-progress-bar"><div className={`swimlane-progress-fill ${epic.status}`} style={{ width: `${epic.progress}%` }} /></div>
                <div className="swimlane-progress-row"><span>{epic.status}</span><span>{epic.progress}%</span></div>
              </div>
              <div className="swimlane-foot">
                <div className="swimlane-crew">
                  {epic.crew.map((id) => {
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
                <span className="swimlane-cost">${epic.cost.toFixed(2)}</span>
              </div>
            </div>
            {columns.map((c) => {
              const cellCards = filtered.filter((card) => card.epic === epic.id && card.col === c.id);
              return <SwimlaneCell key={c.id} epic={epic} col={c.id} cards={cellCards} />;
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
