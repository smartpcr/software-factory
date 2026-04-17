import { describe, it, expect } from 'vitest';
import { isConnectionAllowed, getOutgoingPorts, getIncomingPorts } from '../../ports/portMatrix';

describe('portMatrix', () => {
  it('allows Complete connection between Noop nodes', () => {
    expect(isConnectionAllowed('Noop', 'Noop', 'Complete')).toBe('allowed');
  });

  it('forbids Next connection from Noop', () => {
    expect(isConnectionAllowed('Noop', 'Noop', 'Next')).toBe('forbidden');
  });

  it('allows Next from ForEach (body edge)', () => {
    expect(isConnectionAllowed('ForEach', 'Noop', 'Next')).toBe('allowed');
  });

  it('allows Complete from ForEach (exit edge)', () => {
    expect(isConnectionAllowed('ForEach', 'Noop', 'Complete')).toBe('allowed');
  });

  it('getOutgoingPorts returns ports for all 13 kinds', () => {
    const kinds = [
      'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
      'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
      'While', 'Switch', 'Subflow', 'Timer', 'Container',
    ] as const;
    for (const kind of kinds) {
      expect(getOutgoingPorts(kind).length).toBeGreaterThan(0);
    }
  });

  it('getIncomingPorts returns ports for all 13 kinds', () => {
    const kinds = [
      'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
      'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
      'While', 'Switch', 'Subflow', 'Timer', 'Container',
    ] as const;
    for (const kind of kinds) {
      expect(getIncomingPorts(kind).length).toBeGreaterThan(0);
    }
  });
});
