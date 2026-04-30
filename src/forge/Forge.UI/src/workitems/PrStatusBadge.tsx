// Phase 25 — PR status badge. Shows a compact pill indicating the linked PR's
// current phase: implementation, review, build, or merged. Rendered on exec
// board cards to give at-a-glance PR visibility.

import type { PrStatus } from './workItemTypes';

interface Props {
  status: PrStatus;
  prId?: string | null;
  size?: 'sm' | 'md';
}

export function PrStatusBadge({ status, prId, size = 'md' }: Props) {
  const palette = PR_PALETTE[status] ?? PR_PALETTE.implementation;
  const fontSize = size === 'sm' ? 9 : 10;
  const padX = size === 'sm' ? 5 : 7;
  const padY = size === 'sm' ? 0 : 1;

  return (
    <span
      className="pr-status-badge"
      title={prId ? `${prId} — ${status}` : status}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: `${padY}px ${padX}px`,
        borderRadius: 999,
        border: `1px solid ${palette.border}`,
        background: palette.bg,
        color: palette.fg,
        fontFamily: 'var(--font-mono)',
        fontSize,
        fontWeight: 600,
        letterSpacing: 0.4,
        whiteSpace: 'nowrap',
      }}
    >
      <svg width={size === 'sm' ? 9 : 10} height={size === 'sm' ? 9 : 10} viewBox="0 0 12 12" fill="none" aria-hidden>
        {status === 'merged' ? (
          <path d="M3 3v6M9 3v3a3 3 0 0 1-3 3" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
        ) : (
          <path d="M3 3v6M9 9V6a3 3 0 0 0-3-3" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
        )}
        <circle cx="3" cy="3" r="1.3" fill="currentColor" />
        <circle cx={status === 'merged' ? '3' : '9'} cy="9" r="1.3" fill="currentColor" />
      </svg>
      {PR_LABEL[status]}
    </span>
  );
}

const PR_LABEL: Record<PrStatus, string> = {
  implementation: 'impl',
  review: 'review',
  build: 'build',
  merged: 'merged',
};

const PR_PALETTE: Record<PrStatus, { fg: string; bg: string; border: string }> = {
  implementation: { fg: 'var(--warning)',  bg: 'var(--warning-bg)',  border: 'var(--warning-border)' },
  review:         { fg: 'var(--info)',     bg: 'var(--info-bg)',     border: 'var(--info-border)' },
  build:          { fg: 'var(--accent)',   bg: 'var(--accent-bg)',   border: 'var(--accent)' },
  merged:         { fg: 'var(--success)',  bg: 'var(--success-bg)',  border: 'var(--success-border)' },
};
