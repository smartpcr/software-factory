// Verbatim port of the prototype's CTA card (lines 3913-3922).
export function NewWorkstreamCta({ onClick }: { onClick: () => void }) {
  return (
    <div className="ws-card ws-card-new" onClick={onClick}>
      <svg width="32" height="32" viewBox="0 0 32 32" fill="none">
        <circle cx="16" cy="16" r="14" stroke="currentColor" strokeWidth="1.4" strokeDasharray="3 3" />
        <path d="M16 10 V 22 M 10 16 H 22" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      </svg>
      <div className="label">Spawn workstream</div>
      <div className="sub">Pick an epic or backlog item to spin up an isolated worktree, branch, and assigned crew.</div>
    </div>
  );
}
