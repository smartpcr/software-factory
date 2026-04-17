import type { RuntimeType } from '../../engine-contract';
import { getRegistryEntries, type PaletteEntry } from '../nodes/nodeKindRegistry';

export interface PaletteGroup {
  group: 'task' | 'control' | 'composition';
  label: string;
  entries: PaletteEntry[];
}

export function buildPaletteCatalog(): PaletteGroup[] {
  const entries = getRegistryEntries();
  const groups: PaletteGroup[] = [
    { group: 'task', label: 'Tasks', entries: [] },
    { group: 'control', label: 'Control Flow', entries: [] },
    { group: 'composition', label: 'Composition', entries: [] },
  ];

  for (const entry of entries) {
    const target = groups.find((g) => g.group === entry.icon.group);
    if (target) target.entries.push(entry);
  }

  return groups.filter((g) => g.entries.length > 0);
}

export function getPaletteEntryByKind(kind: RuntimeType): PaletteEntry | undefined {
  return getRegistryEntries().find((e) => e.runtimeType === kind);
}