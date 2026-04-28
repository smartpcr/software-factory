// Phase 19 — Execution status badge. Shows a coloured pill with an optional
// trial counter for work items that have been through the queue at least once.
// Statuses: idle (hidden), queued, live (pulsing), completed, failed, cancelled.

interface Props {
  status?: string;
  trialCount?: number;
  size?: 'sm' | 'md';
}

export function ExecBadge({ status, trialCount = 0, size = 'md' }: Props) {
  if (!status || status === 'idle') return null;

  const palette = EXEC_PALETTE[status] ?? EXEC_PALETTE.idle;
  const padY = size === 'sm' ? '0' : '1px';
  const padX = size === 'sm' ? 5 : 7;
  const fontSize = size === 'sm' ? 9 : 10;
  const dotSize = size === 'sm' ? 5 : 6;

  return (
    <span
      className={status === 'live' ? 'exec-badge-live' : undefined}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: `${padY} ${padX}px`,
        borderRadius: 999,
        border: `1px solid ${palette.border}`,
        background: palette.bg,
        color: palette.fg,
        fontFamily: 'var(--font-mono)',
        fontSize,
        fontWeight: 600,
        letterSpacing: 0.5,
        textTransform: 'lowercase',
        whiteSpace: 'nowrap',
      }}
    >
      <span
        aria-hidden
        style={{
          width: dotSize,
          height: dotSize,
          borderRadius: 999,
          background: palette.fg,
          ...(status === 'live' ? { animation: 'exec-pulse 1.4s ease-in-out infinite' } : {}),
        }}
      />
      {status}
      {trialCount > 1 && (
        <span style={{ opacity: 0.7, fontSize: fontSize - 1 }}>
          ×{trialCount}
        </span>
      )}
    </span>
  );
}

const EXEC_PALETTE: Record<string, { fg: string; bg: string; border: string }> = {
  idle:      { fg: 'var(--text-tertiary)', bg: 'transparent',            border: 'var(--border)' },
  queued:    { fg: 'var(--warning)',        bg: 'var(--warning-bg)',      border: 'var(--warning-border)' },
  live:      { fg: '#10b981',              bg: 'rgba(16,185,129,0.08)',  border: 'rgba(16,185,129,0.25)' },
  completed: { fg: 'var(--success)',        bg: 'var(--success-bg)',      border: 'var(--success-border)' },
  failed:    { fg: 'var(--danger)',         bg: 'var(--danger-bg)',       border: 'var(--danger-border)' },
  cancelled: { fg: 'var(--text-tertiary)', bg: 'transparent',            border: 'var(--border-strong)' },
};
