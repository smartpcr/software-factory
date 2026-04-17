import { describe, it, expect, beforeEach } from 'vitest';
import { createNodeFromKind, resetNodeCounter } from '../../palette/createNodeFromKind';
import type { RuntimeType } from '../../../../engine-contract';

describe('createNodeFromKind', () => {
  beforeEach(() => {
    resetNodeCounter();
  });

  it('creates nodes for all 13 kinds', () => {
    const kinds: RuntimeType[] = [
      'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
      'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
      'While', 'Switch', 'Subflow', 'Timer', 'Container',
    ];

    for (const kind of kinds) {
      const node = createNodeFromKind(kind);
      expect(node.runtimeType).toBe(kind);
      expect(node.nodeId).toBeTruthy();
      expect(node.joinType).toBe('Any');
      expect(node.priority).toBe('Normal');
    }
  });

  it('generates unique node IDs', () => {
    const ids = new Set<string>();
    for (let i = 0; i < 5; i++) {
      ids.add(createNodeFromKind('Noop').nodeId);
    }
    expect(ids.size).toBe(5);
  });

  it('ForEach has engine defaults', () => {
    const node = createNodeFromKind('ForEach');
    if (node.runtimeType === 'ForEach') {
      expect(node.itemVariableName).toBe('item');
    }
  });

  it('While has engine defaults', () => {
    const node = createNodeFromKind('While');
    if (node.runtimeType === 'While') {
      expect(node.maxIterations).toBe(1000);
    }
  });

  it('Subflow has engine defaults', () => {
    const node = createNodeFromKind('Subflow');
    if (node.runtimeType === 'Subflow') {
      expect(node.timeout).toBe('00:05:00');
      expect(node.skipValidation).toBe(false);
    }
  });

  it('Container defaults to Sequential', () => {
    const node = createNodeFromKind('Container');
    if (node.runtimeType === 'Container') {
      expect(node.executionMode).toBe('Sequential');
    }
  });
});
