import { describe, it, expect } from 'vitest';
import { getRegistryEntries, getRegistryEntry } from '../../nodes/nodeKindRegistry';

describe('nodeKindRegistry', () => {
  it('returns exactly 13 entries', () => {
    expect(getRegistryEntries()).toHaveLength(13);
  });

  it('every entry has runtimeType, icon, and description', () => {
    for (const entry of getRegistryEntries()) {
      expect(entry.runtimeType).toBeTruthy();
      expect(entry.icon).toBeDefined();
      expect(entry.icon.label).toBeTruthy();
      expect(entry.icon.emoji).toBeTruthy();
      expect(entry.icon.color).toBeTruthy();
      expect(entry.icon.group).toBeTruthy();
      expect(entry.description).toBeTruthy();
    }
  });

  it('getRegistryEntry returns correct entry', () => {
    const entry = getRegistryEntry('ForEach');
    expect(entry.runtimeType).toBe('ForEach');
    expect(entry.icon.group).toBe('control');
  });

  it('covers all runtime types', () => {
    const types = getRegistryEntries().map((e) => e.runtimeType);
    expect(types).toContain('Noop');
    expect(types).toContain('Container');
    expect(types).toContain('Timer');
    expect(new Set(types).size).toBe(13);
  });
});
