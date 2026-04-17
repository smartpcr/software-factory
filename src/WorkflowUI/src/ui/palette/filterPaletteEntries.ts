import type { PaletteEntry } from '../nodes/nodeKindRegistry';

export function filterPaletteEntries(
  entries: PaletteEntry[],
  query: string,
): PaletteEntry[] {
  const q = query.trim().toLowerCase();
  if (!q) return entries;

  return entries.filter(
    (entry) =>
      entry.runtimeType.toLowerCase().includes(q) ||
      entry.icon.label.toLowerCase().includes(q) ||
      entry.description.toLowerCase().includes(q),
  );
}