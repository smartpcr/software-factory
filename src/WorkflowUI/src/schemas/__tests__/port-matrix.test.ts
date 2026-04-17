import { describe, it, expect } from 'vitest';
import { outgoingPortMatrix, incomingPortMatrix, classifyOutgoing } from '../port-matrix';
import type { RuntimeType } from '../../engine-contract.generated';
import portMatrixFixture from '../../__fixtures__/port-matrix.json';
import enumFixture from '../../__fixtures__/engine-enums.json';

describe('Port matrix', () => {
  const allKinds = (enumFixture as Record<string, { members: string[] }>).RuntimeType.members;

  it('outgoingPortMatrix covers all 13 kinds', () => {
    expect(Object.keys(outgoingPortMatrix)).toHaveLength(13);
    for (const kind of allKinds) {
      expect(outgoingPortMatrix[kind as RuntimeType]).toBeDefined();
    }
  });

  it('incomingPortMatrix covers all 13 kinds', () => {
    expect(Object.keys(incomingPortMatrix)).toHaveLength(13);
    for (const kind of allKinds) {
      expect(incomingPortMatrix[kind as RuntimeType]).toBeDefined();
    }
  });

  it('classifyOutgoing returns correct ports for Noop', () => {
    const ports = classifyOutgoing('Noop');
    expect(ports).toHaveLength(1);
    expect(ports[0].messageType).toBe('Complete');
  });

  it('classifyOutgoing returns correct ports for IfElse', () => {
    const ports = classifyOutgoing('IfElse');
    expect(ports).toHaveLength(2);
  });

  it('classifyOutgoing returns correct ports for ForEach', () => {
    const ports = classifyOutgoing('ForEach');
    expect(ports).toHaveLength(2);
    expect(ports[0].messageType).toBe('Next');
    expect(ports[1].messageType).toBe('Complete');
  });

  it('fixture matches outgoingPortMatrix', () => {
    const fixtureObj = portMatrixFixture as Record<string, { outgoing: Array<{ messageType: string; label: string }> }>;
    for (const kind of allKinds) {
      const expected = fixtureObj[kind]?.outgoing ?? [];
      const actual = outgoingPortMatrix[kind as RuntimeType];
      expect(actual).toHaveLength(expected.length);
      for (let i = 0; i < expected.length; i++) {
        expect(actual[i].messageType).toBe(expected[i].messageType);
        expect(actual[i].label).toBe(expected[i].label);
      }
    }
  });

  it('every port entry has non-empty label', () => {
    for (const kind of allKinds) {
      const ports = outgoingPortMatrix[kind as RuntimeType];
      for (const p of ports) {
        expect(p.label.length).toBeGreaterThan(0);
      }
    }
  });

  it('every port entry has valid messageType', () => {
    const validTypes = new Set(['Complete', 'Fail', 'Progress', 'Next', 'Cancel']);
    for (const kind of allKinds) {
      for (const p of outgoingPortMatrix[kind as RuntimeType]) {
        expect(validTypes.has(p.messageType)).toBe(true);
      }
    }
  });
});
