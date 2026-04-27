import type { Pr } from '@/types';
import { PrCard } from './PrCard';

interface Props {
  prs: Pr[];
  onOpen: (pr: Pr) => void;
  onApprove?: (pr: Pr) => void;
  onRequestChanges?: (pr: Pr) => void;
}

// Verbatim port of the prototype's pcols + renderPRs (lines 3936-3968).
// The prototype renders THREE columns (Implementation / Critical review / Build & merge).
// We keep that exact mapping; PRs whose col === 'merged' won't appear (the
// prototype doesn't show merged PRs on this view either).
const PCOLS: Array<{ id: 'implementation' | 'review' | 'build'; name: string; sub: string }> = [
  { id: 'implementation', name: 'Implementation', sub: 'agent is writing' },
  { id: 'review',         name: 'Critical review', sub: 'critic + human gate' },
  { id: 'build',          name: 'Build & merge',   sub: 'CI + auto-merge' },
];

export function PrBoard({ prs, onOpen, onApprove, onRequestChanges }: Props) {
  return (
    <div className="pr-board" id="pr-board">
      {PCOLS.map((pc) => {
        const colPRs = prs.filter((p) => p.col === pc.id);
        return (
          <div className={`pr-col ${pc.id}`} key={pc.id} data-col={pc.id}>
            <div className="pr-col-head">
              <div className="pr-col-head-row">
                <span className="pr-col-name"><span className="marker" />{pc.name}</span>
                <span className="pr-col-count">{colPRs.length}</span>
              </div>
              <div className="pr-col-sub">{pc.sub}</div>
            </div>
            <div className="pr-col-body">
              {colPRs.length === 0 ? (
                <div className="cell-empty" style={{ minHeight: 60 }}>no PRs</div>
              ) : (
                colPRs.map((pr) => (
                  <PrCard
                    key={pr.id}
                    pr={pr}
                    onOpen={onOpen}
                    onApprove={onApprove}
                    onRequestChanges={onRequestChanges}
                  />
                ))
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
