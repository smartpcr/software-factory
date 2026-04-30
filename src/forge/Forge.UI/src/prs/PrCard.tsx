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
// Phase 25 — redesigned for richer visuals with sparse ADO/GitHub data.
export function PrCard({ pr, onOpen }: Props) {
  const total = pr.additions + pr.deletions || 1;
  const addPct = (pr.additions / total) * 100;
  const delPct = (pr.deletions / total) * 100;
  const hasChanges = pr.additions > 0 || pr.deletions > 0;
  const ageClass = ageUrgency(pr.age);

  const cls = [
    'pr-card',
    pr.build && pr.build.status === 'failing' ? 'failing' : '',
    pr.conflicts ? 'conflicts' : '',
  ].filter(Boolean).join(' ');

  const openComments = pr.openCommentCount ?? 0;
  const attentionComments = pr.attentionCommentCount ?? 0;

  return (
    <div className={cls} data-pr-id={pr.id} onClick={() => onOpen(pr)}>
      {/* Accent stripe — left edge color by column */}
      <div className={`pr-card-stripe pr-card-stripe-${pr.col}`} />

      <div className="pr-card-head">
        <div className="pr-card-id-row">
          <span className="pr-card-id">{pr.id}</span>
          {pr.provider && <ProviderPill kind={pr.provider} />}
          <span className={`pr-card-age ${ageClass}`} title={`Created ${pr.age} ago`}>{pr.age}</span>
        </div>
        <div className="pr-card-title">{pr.title || '(untitled)'}</div>
      </div>

      <div className="pr-card-body">
        {/* Author + branch row — always visible */}
        <div className="pr-card-info-row">
          <span className="pr-card-info-author" title={pr.author.name}>
            <AgentChip id={pr.author.id} name={pr.author.name} role={roleFor(pr.author.id)} />
            {pr.author.name}
          </span>
          {pr.branch && (
            <span className="pr-card-info-branch" title={`${pr.branch} → ${pr.base}`}>
              {pr.branch}
            </span>
          )}
        </div>

        {/* Comment badges */}
        {(openComments > 0 || attentionComments > 0) && (
          <div className="pr-card-info-row">
            {openComments > 0 && (
              <span className="pr-card-comment-chip" title={`${openComments} open comment${openComments === 1 ? '' : 's'}`}>
                💬 {openComments}
              </span>
            )}
            {attentionComments > 0 && (
              <span className="pr-card-attention-chip" data-testid="pr-card-attention"
                title={`${attentionComments} need response`}>
                ⚠ {attentionComments}
              </span>
            )}
          </div>
        )}

        {hasChanges && (
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
        )}

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
          Auto-merge armed
        </div>
      )}
    </div>
  );
}

/** Provider pill — small ADO/GH badge next to the PR id. */
function ProviderPill({ kind }: { kind: string }) {
  const isAdo = kind === 'ado';
  return (
    <span className={`pr-card-provider ${kind}`}>
      {isAdo ? (
        <svg width="10" height="10" viewBox="0 0 16 16" fill="currentColor" aria-hidden>
          <path d="M15 3.62v8.55L11.5 14l-4.63-1.65v1.6L3.28 9.9l8.76.7V4.56L15 3.62zM12.04 4.5L7.2 2v1.94L2.77 5.27 1 7.35v4.84L3.28 13V6.18l8.76-1.68z" />
        </svg>
      ) : (
        <svg width="10" height="10" viewBox="0 0 16 16" fill="currentColor" aria-hidden>
          <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" />
        </svg>
      )}
      {isAdo ? 'ADO' : 'GH'}
    </span>
  );
}

/** Map age string to urgency class for color treatment. */
function ageUrgency(age: string): string {
  if (!age || age === '—') return '';
  const val = parseInt(age, 10);
  if (Number.isNaN(val)) return '';
  if (age.endsWith('d') && val > 30) return 'age-stale';
  if (age.endsWith('d') && val > 7) return 'age-aging';
  return 'age-fresh';
}

// Phase 24.12 — comment + sync badges (now inlined in PrCard body).

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
