import type { Pr } from '@/types';
import { PrCard } from './PrCard';

export type PrBoardLayout = 'flow' | 'lifecycle';

interface Props {
  prs: Pr[];
  layout?: PrBoardLayout;
  onOpen: (pr: Pr) => void;
  onApprove?: (pr: Pr) => void;
  onRequestChanges?: (pr: Pr) => void;
}

interface ColDef {
  id: string;
  name: string;
  sub: string;
}

// Existing 3-column "agent flow" view — preserved for backwards compatibility.
// PRs whose col === 'merged' don't appear here (the prototype doesn't show
// merged PRs in the agent-flow view either).
const FLOW_COLS: ColDef[] = [
  { id: 'implementation', name: 'Implementation', sub: 'agent is writing' },
  { id: 'review',         name: 'Critical review', sub: 'critic + human gate' },
  { id: 'build',          name: 'Build & merge',   sub: 'CI + auto-merge' },
];

// ADO PR lifecycle — Draft → In Review → Approved → Merged. Mirrors the four
// states surfaced in the ADO web UI's "Pull requests" page, derived from the
// fields we already have on Pr (col, review.verdict, autoMerge).
const LIFECYCLE_COLS: ColDef[] = [
  { id: 'draft',    name: 'Draft',     sub: 'work in progress' },
  { id: 'review',   name: 'In Review', sub: 'open · awaiting reviewers' },
  { id: 'approved', name: 'Approved',  sub: 'ready to merge' },
  { id: 'merged',   name: 'Merged',    sub: 'completed' },
];

/** Map a PR onto an ADO lifecycle column from the fields we already have. */
function classifyLifecycle(pr: Pr): string {
  if (pr.col === 'merged') return 'merged';
  if (pr.col === 'implementation') return 'draft';
  const approved = pr.review?.verdict === 'approved' || pr.autoMerge === true;
  if (approved) return 'approved';
  return 'review';
}

export function PrBoard({ prs, layout = 'flow', onOpen, onApprove, onRequestChanges }: Props) {
  const cols = layout === 'lifecycle' ? LIFECYCLE_COLS : FLOW_COLS;
  const bucket = layout === 'lifecycle' ? classifyLifecycle : ((p: Pr) => p.col);

  return (
    <div className={`pr-board pr-board-${cols.length}col`} id="pr-board" data-layout={layout}>
      {cols.map((pc) => {
        const colPRs = prs
          .filter((p) => bucket(p) === pc.id)
          .sort((a, b) => {
            // Most recently updated first.
            const aTime = a.updatedAt ?? a.createdAt ?? '';
            const bTime = b.updatedAt ?? b.createdAt ?? '';
            return bTime.localeCompare(aTime);
          });
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
