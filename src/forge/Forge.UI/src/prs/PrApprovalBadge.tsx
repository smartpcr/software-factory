import { verdictClass, verdictLabel, type ApprovalVerdict } from './prVerdict';

// Phase 28.2 — small approve / rejected / changes pill rendered on
// the PR card and at the top of the PR detail panel. Visual is
// deliberately less "loud" than the build status pill because there
// can be both a green build and a red review or vice versa.

interface Props {
  verdict: ApprovalVerdict | null;
  /** Pass `false` to render `null` for `pending` (used on the PR card
   *  where we don't want a chip on every PR that just has nobody
   *  signed off yet). The detail panel passes `true` so reviewers see
   *  "pending" explicitly. */
  showPending?: boolean;
}

export function PrApprovalBadge({ verdict, showPending = false }: Props) {
  if (!verdict) return null;
  if (verdict === 'pending' && !showPending) return null;
  return (
    <span
      className={`pr-verdict ${verdictClass(verdict)}`}
      data-testid="pr-verdict-badge"
      data-verdict={verdict}
    >
      {verdictLabel(verdict)}
    </span>
  );
}
