import type { WorkItemState } from './workItemTypes';

interface Props {
  state: WorkItemState;
  size?: 'sm' | 'md';
}

// Phase 12 — lowercase state pill drawing on the existing semantic palette.
// proposed = warning (amber), active = info (deep blue), resolved = success
// (forest), closed = muted text-tertiary. Lowercase + monospace gives the
// editorial feel: looks like the catalog markers in a museum index card.
export function StatePill({ state, size = 'md' }: Props) {
  const palette = STATE_PALETTE[state];
  const padY = size === 'sm' ? '0' : '1px';
  const padX = size === 'sm' ? 6 : 8;
  return (
    <span
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 5,
        padding: `${padY} ${padX}px`,
        borderRadius: 999,
        border: `1px solid ${palette.border}`,
        background: palette.bg,
        color: palette.fg,
        fontFamily: 'var(--font-mono)',
        fontSize: size === 'sm' ? 9.5 : 10.5,
        fontWeight: 600,
        letterSpacing: 0.6,
        textTransform: 'lowercase',
        whiteSpace: 'nowrap',
      }}
    >
      <span
        aria-hidden
        style={{
          width: 6, height: 6, borderRadius: 999, background: palette.fg,
          opacity: state === 'closed' ? 0.5 : 1,
        }}
      />
      {state}
    </span>
  );
}

const STATE_PALETTE: Record<WorkItemState, { fg: string; bg: string; border: string }> = {
  proposed: { fg: 'var(--warning)', bg: 'var(--warning-bg)', border: 'var(--warning-border)' },
  active:   { fg: 'var(--info)',    bg: 'var(--info-bg)',    border: 'var(--info-border)' },
  resolved: { fg: 'var(--success)', bg: 'var(--success-bg)', border: 'var(--success-border)' },
  closed:   { fg: 'var(--text-tertiary)', bg: 'transparent', border: 'var(--border-strong)' },
};
