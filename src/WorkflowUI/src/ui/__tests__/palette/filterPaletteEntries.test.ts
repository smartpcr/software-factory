import { describe, it, expect } from 'vitest';
import { filterPaletteEntries } from '../../palette/filterPaletteEntries';
import { getRegistryEntries } from '../../nodes/nodeKindRegistry';

describe('filterPaletteEntries', () => {
  const all = getRegistryEntries();

  it('returns all entries for empty query', () => {
    expect(filterPaletteEntries(all, '')).toHaveLength(13);
    expect(filterPaletteEntries(all, '  ')).toHaveLength(13);
  });

  it('filters by runtime type name', () => {
    const result = filterPaletteEntries(all, 'noop');
    expect(result).toHaveLength(1);
    expect(result[0].runtimeType).toBe('Noop');
  });

  it('filters by label', () => {
    const result = filterPaletteEntries(all, 'powershell');
    expect(result.length).toBeGreaterThanOrEqual(2); // PowerShell + PowerShellTask
  });

  it('filters by description', () => {
    const result = filterPaletteEntries(all, 'iterates');
    expect(result).toHaveLength(1);
    expect(result[0].runtimeType).toBe('ForEach');
  });

  it('returns empty for non-matching query', () => {
    expect(filterPaletteEntries(all, 'xyznonexistent')).toHaveLength(0);
  });

  it('is case-insensitive', () => {
    const lower = filterPaletteEntries(all, 'csharp');
    const upper = filterPaletteEntries(all, 'CSHARP');
    expect(lower.length).toBe(upper.length);
  });
});
