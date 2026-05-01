// Shared display mapping for sync run Kind values.

export interface SyncKindDisplay {
  category: string;
  trigger: string;
  label: string;
  color: string;
  bgColor: string;
  borderColor: string;
}

const BADGE_STYLE = {
  color: 'var(--text-secondary)',
  bgColor: 'var(--bg-surface-2)',
  borderColor: 'var(--border)',
};

const MAP: Record<string, SyncKindDisplay> = {
  delta:       { category: 'Work Item',     trigger: 'scheduled', label: 'Work Item (scheduled)',     ...BADGE_STYLE },
  full:        { category: 'Work Item',     trigger: 'scheduled', label: 'Work Item (scheduled)',     ...BADGE_STYLE },
  manual:      { category: 'Work Item',     trigger: 'manual',    label: 'Work Item (manual)',        ...BADGE_STYLE },
  'pr-delta':  { category: 'Pull Request',  trigger: 'scheduled', label: 'Pull Request (scheduled)',  ...BADGE_STYLE },
  'pr-manual': { category: 'Pull Request',  trigger: 'manual',    label: 'Pull Request (manual)',     ...BADGE_STYLE },
  build:       { category: 'Builds',        trigger: 'scheduled', label: 'Builds (scheduled)',        ...BADGE_STYLE },
};

const FALLBACK: SyncKindDisplay = {
  category: 'Other', trigger: 'unknown', label: 'Other',
  color: 'var(--text-tertiary)', bgColor: 'var(--bg-surface-2)', borderColor: 'var(--border)',
};

export function syncKindDisplay(kind: string): SyncKindDisplay {
  return MAP[kind] ?? { ...FALLBACK, label: kind };
}
