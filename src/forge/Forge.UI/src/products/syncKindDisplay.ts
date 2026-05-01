// Shared display mapping for sync run Kind values.

export interface SyncKindDisplay {
  category: string;
  trigger: string;
  label: string;
  color: string;
  bgColor: string;
  borderColor: string;
}

const MAP: Record<string, SyncKindDisplay> = {
  delta:       { category: 'Work Item',     trigger: 'scheduled', label: 'Work Item (scheduled)',     color: 'var(--text-tertiary)',  bgColor: 'var(--bg-surface-2)',                                    borderColor: 'var(--border)' },
  full:        { category: 'Work Item',     trigger: 'scheduled', label: 'Work Item (scheduled)',     color: 'var(--info)',           bgColor: 'var(--info-bg)',                                         borderColor: 'var(--info-border)' },
  manual:      { category: 'Work Item',     trigger: 'manual',    label: 'Work Item (manual)',        color: 'var(--warning)',        bgColor: 'var(--warning-bg)',                                      borderColor: 'var(--warning-border)' },
  'pr-delta':  { category: 'Pull Request',  trigger: 'scheduled', label: 'Pull Request (scheduled)',  color: 'var(--text-tertiary)',  bgColor: 'var(--bg-surface-2)',                                    borderColor: 'var(--border)' },
  'pr-manual': { category: 'Pull Request',  trigger: 'manual',    label: 'Pull Request (manual)',     color: 'var(--warning)',        bgColor: 'var(--warning-bg)',                                      borderColor: 'var(--warning-border)' },
  build:       { category: 'Builds',        trigger: 'scheduled', label: 'Builds (scheduled)',        color: 'var(--accent)',         bgColor: 'color-mix(in srgb, var(--accent) 10%, transparent)',      borderColor: 'var(--accent)' },
};

const FALLBACK: SyncKindDisplay = {
  category: 'Other', trigger: 'unknown', label: 'Other',
  color: 'var(--text-tertiary)', bgColor: 'var(--bg-surface-2)', borderColor: 'var(--border)',
};

export function syncKindDisplay(kind: string): SyncKindDisplay {
  return MAP[kind] ?? { ...FALLBACK, label: kind };
}
