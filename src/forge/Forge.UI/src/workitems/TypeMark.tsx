import type { WorkItemType } from './workItemTypes';
import { typeMark, typeLabel } from './workItemTypes';

interface Props {
  type: WorkItemType;
  size?: number;
  /** When true, renders a hollow/outlined variant — used for inactive states. */
  muted?: boolean;
}

// Phase 12 — single-letter typographic stamp identifying the work item type.
// E F S T B in JetBrains Mono. The hue rotates by type so a glance across a
// board / list still reads each row's nature without text. All hues drawn
// from the existing forge palette: epic = accent (ochre), feature = info,
// story = success, task = text-secondary, bug = danger.
export function TypeMark({ type, size = 22, muted = false }: Props) {
  const palette = TYPE_PALETTE[type] ?? FALLBACK_PALETTE;
  return (
    <span
      title={typeLabel(type)}
      aria-label={typeLabel(type)}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        width: size,
        height: size,
        flexShrink: 0,
        borderRadius: 'var(--radius-sm)',
        fontFamily: 'var(--font-mono)',
        fontSize: size <= 18 ? 10 : size * 0.5,
        fontWeight: 700,
        letterSpacing: 0.5,
        color: muted ? 'var(--text-tertiary)' : palette.fg,
        background: muted ? 'transparent' : palette.bg,
        border: `1px solid ${muted ? 'var(--border-strong)' : palette.border}`,
        userSelect: 'none',
      }}
    >
      {typeMark(type)}
    </span>
  );
}

const FALLBACK_PALETTE = { fg: 'var(--text-secondary)', bg: 'var(--bg-surface-2)', border: 'var(--border-strong)' };

const TYPE_PALETTE: Record<WorkItemType, { fg: string; bg: string; border: string }> = {
  // Dev ladder.
  epic:    { fg: 'var(--accent)',  bg: 'var(--accent-bg)',  border: 'var(--border-accent)' },
  feature: { fg: 'var(--info)',    bg: 'var(--info-bg)',    border: 'var(--info-border)' },
  task:    { fg: 'var(--text-secondary)', bg: 'var(--bg-surface-2)', border: 'var(--border-strong)' },
  bug:     { fg: 'var(--danger)',  bg: 'var(--danger-bg)',  border: 'var(--danger-border)' },
  // Design ladder — borrows complementary tones so a glance separates
  // the two tracks at the type-mark level. Phase = info-tinted on accent
  // glow; stage = success; step = text-secondary outlined.
  phase:   { fg: 'var(--accent)',  bg: 'transparent',       border: 'var(--accent)' },
  stage:   { fg: 'var(--success)', bg: 'var(--success-bg)', border: 'var(--success-border)' },
  step:    { fg: 'var(--text-secondary)', bg: 'transparent', border: 'var(--border-strong)' },
};
