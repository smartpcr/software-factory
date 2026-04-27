import type { Pr } from '@/types';
import { avatarFor, agentTooltip } from '@/agentAvatar';

function AgentChip({ id, name, role }: { id: string; name?: string | null; role?: string | null }) {
  const avatar = avatarFor(id, name);
  const tooltip = agentTooltip(id, name, role);
  if (avatar) {
    return (
      <img
        src={avatar}
        alt={name || id}
        className={`agent-chip-avatar ${role ?? ''}`}
        style={{ borderColor: 'var(--bg-sunken)' }}
        title={tooltip}
      />
    );
  }
  return <span className={`agent-chip ${role ?? ''}`} style={{ borderColor: 'var(--bg-sunken)' }} title={tooltip}>{id}</span>;
}

interface Props {
  pr: Pr;
  onOpen: (pr: Pr) => void;
  onApprove?: (pr: Pr) => void;
  onRequestChanges?: (pr: Pr) => void;
}

function roleFor(id: string): string {
  if (id.startsWith('A')) return 'arch';
  if (id.startsWith('E')) return 'eng';
  if (id.startsWith('R')) return 'rev';
  if (id.startsWith('Q')) return 'qa';
  return 'human';
}

// Verbatim port of the prototype's renderPRCard (lines 3970-4089).
export function PrCard({ pr, onOpen, onApprove, onRequestChanges }: Props) {
  const total = pr.additions + pr.deletions || 1;
  const addPct = (pr.additions / total) * 100;
  const delPct = (pr.deletions / total) * 100;

  const cls = [
    'pr-card',
    pr.build && pr.build.status === 'failing' ? 'failing' : '',
    pr.conflicts ? 'conflicts' : '',
  ].filter(Boolean).join(' ');

  return (
    <div className={cls} data-pr-id={pr.id}>
      <div className="pr-card-head">
        <div className="pr-card-id-row">
          <span className="pr-card-id">{pr.id}</span>
          <span className={`pr-card-epic ${pr.epicColor}`}>{pr.epic}</span>
          <span className="pr-card-age">{pr.age}</span>
        </div>
        <div className="pr-card-title">{pr.title}</div>
        <div className="pr-card-branch">
          <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
            <circle cx="2.5" cy="2" r="1" stroke="currentColor" strokeWidth="1.2" fill="none" />
            <circle cx="2.5" cy="7.5" r="1" stroke="currentColor" strokeWidth="1.2" fill="none" />
            <circle cx="7.5" cy="5" r="1" stroke="currentColor" strokeWidth="1.2" fill="none" />
            <line x1="2.5" y1="3" x2="2.5" y2="6.5" stroke="currentColor" strokeWidth="1.2" />
            <path d="M7.5 4 V 3.5 a 1.5 1.5 0 0 0 -1.5 -1.5 H 3.5" stroke="currentColor" strokeWidth="1.2" fill="none" />
          </svg>
          {pr.branch} <span className="arrow">→</span> {pr.base}
          {pr.conflicts && <span className="conflict-badge">conflict</span>}
        </div>
      </div>

      <div className="pr-card-body">
        <div className="pr-card-changes">
          <div className="pr-changes-bar">
            <div className="bar-add" style={{ width: `${addPct}%` }} />
            <div className="bar-del" style={{ width: `${delPct}%` }} />
          </div>
          <span className="add">+{pr.additions}</span>
          <span className="del">−{pr.deletions}</span>
          <span className="files">
            {pr.files} file{pr.files === 1 ? '' : 's'} · {pr.commits} commit{pr.commits === 1 ? '' : 's'}
          </span>
        </div>

        {pr.impl && <PrImplStrip impl={pr.impl} />}
        {pr.build && <PrBuildStrip build={pr.build} />}
        {pr.review && <PrReviewBlock review={pr.review} />}
      </div>

      {pr.conflicts && (
        <div className="pr-card-conflict">
          <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
            <path d="M5 1 L 9 8 H 1 Z" stroke="currentColor" strokeWidth="1.4" fill="none" strokeLinejoin="round" />
            <line x1="5" y1="4" x2="5" y2="6" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          </svg>
          Conflict · {pr.conflictPaths || 'merge conflict'}
        </div>
      )}
      {pr.humanGate && (
        <div className="pr-card-human">
          <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
            <path d="M5 1 L 8.5 2.5 V 5.5 C 8.5 7.5 7 9 5 9.5 C 3 9 1.5 7.5 1.5 5.5 V 2.5 Z" stroke="currentColor" strokeWidth="1.3" fill="none" />
          </svg>
          {pr.gateReason}
        </div>
      )}
      {pr.autoMerge && (
        <div
          className="pr-card-human"
          style={{ background: 'var(--info-bg)', borderColor: 'var(--info-border)', color: 'var(--info)' }}
        >
          <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
            <path d="M6 1 L 3 5 H 5 L 4 9 L 7 5 H 5 L 6 1" stroke="currentColor" strokeWidth="1.2" fill="none" strokeLinejoin="round" />
          </svg>
          Auto-merge armed · waiting for green CI
        </div>
      )}

      <div className="pr-card-foot">
        <div className="pr-card-foot-left">
          <AgentChip id={pr.author.id} name={pr.author.name} role={roleFor(pr.author.id)} />
          <span style={{ color: 'var(--text-secondary)' }}>{pr.author.name}</span>
        </div>
        <div className="pr-card-foot-right">
          {pr.col === 'review' && pr.humanGate ? (
            <>
              <button
                className="pr-card-action primary"
                title="Approve & merge"
                onClick={() => onApprove?.(pr)}
              >
                <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
                  <path d="M2 5.5 L 4.5 8 L 9 3" stroke="currentColor" strokeWidth="1.6" fill="none" strokeLinecap="round" />
                </svg>
              </button>
              <button
                className="pr-card-action"
                title="Request changes"
                onClick={() => onRequestChanges?.(pr)}
              >
                <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
                  <path d="M2 5.5 H 9 M 5 2.5 L 2 5.5 L 5 8.5" stroke="currentColor" strokeWidth="1.4" fill="none" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
            </>
          ) : (
            <>
              <button className="pr-card-action" title="Open" onClick={() => onOpen(pr)}>
                <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
                  <path d="M4 2 H 2 V 9 H 9 V 7 M 6 2 H 9 V 5 M 9 2 L 5 6" stroke="currentColor" strokeWidth="1.2" fill="none" strokeLinecap="round" />
                </svg>
              </button>
              <button className="pr-card-action" title="More">
                <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
                  <circle cx="2.5" cy="5.5" r="0.9" fill="currentColor" />
                  <circle cx="5.5" cy="5.5" r="0.9" fill="currentColor" />
                  <circle cx="8.5" cy="5.5" r="0.9" fill="currentColor" />
                </svg>
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function PrBuildStrip({ build }: { build: NonNullable<Pr['build']> }) {
  return (
    <div className="pr-card-build">
      <div className="pr-card-build-row">
        <span className={`step ${build.status}`}>{build.status}</span>
        <span style={{ color: 'var(--text-secondary)' }}>{build.step}</span>
        <span className="meta">{build.percent}%</span>
      </div>
      <div className="pr-build-bar">
        <div className={`pr-build-fill ${build.status}`} style={{ width: `${build.percent}%` }} />
      </div>
    </div>
  );
}

function PrImplStrip({ impl }: { impl: NonNullable<Pr['impl']> }) {
  const blocked = impl.phase === 'blocked';
  return (
    <div
      className="pr-card-build"
      style={{
        borderColor: blocked ? 'var(--danger-border)' : 'var(--info-border)',
        background: blocked ? 'var(--danger-bg)' : 'var(--info-bg)',
      }}
    >
      <div className="pr-card-build-row">
        <span className="step" style={{ color: blocked ? 'var(--danger)' : 'var(--info)' }}>{impl.phase}</span>
        <span style={{ color: 'var(--text-secondary)' }}>{impl.step}</span>
        <span className="meta">{impl.percent}%</span>
      </div>
      <div className="pr-build-bar">
        <div
          className={`pr-build-fill ${blocked ? 'failing' : 'running'}`}
          style={{ width: `${impl.percent}%`, background: blocked ? 'var(--danger)' : 'var(--info)' }}
        />
      </div>
    </div>
  );
}

function PrReviewBlock({ review }: { review: NonNullable<Pr['review']> }) {
  return (
    <div className="pr-card-review">
      <div className="pr-review-summary">
        <AgentChip
          id={review.agent}
          name={review.name}
          role={review.agent.startsWith('R') ? 'rev' : 'eng'}
        />
        <span className="who">{review.name}</span>
        <span className={`verdict ${review.verdict}`}>{review.verdict}</span>
      </div>
      <div className="pr-review-text" dangerouslySetInnerHTML={{ __html: review.summary }} />
    </div>
  );
}
