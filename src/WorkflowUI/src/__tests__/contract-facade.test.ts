import { RUNTIME_TYPES, assertNever, isContainerNode, isSwitchNode, isIfElseNode, isForEachNode, isWhileNode, isSubflowNode } from '../engine-contract';
import type { NodeDto } from '../engine-contract';
import fixture from '../__fixtures__/engine-enums.json';

describe('Contract facade', () => {
  it('exports RUNTIME_TYPES with 13 members', () => {
    expect(RUNTIME_TYPES).toHaveLength(13);
  });

  it('RUNTIME_TYPES matches engine-enums.json fixture', () => {
    expect([...RUNTIME_TYPES]).toEqual(fixture.RuntimeType.members);
  });

  it('assertNever throws with value', () => {
    const x = { runtimeType: 'Mystery' } as never;
    expect(() => assertNever(x)).toThrow(/Mystery/);
  });

  it('isContainerNode returns true for Container', () => {
    const node = { runtimeType: 'Container' } as NodeDto;
    expect(isContainerNode(node)).toBe(true);
  });

  it('isContainerNode returns false for Noop', () => {
    const node = { runtimeType: 'Noop' } as NodeDto;
    expect(isContainerNode(node)).toBe(false);
  });

  it('isSwitchNode returns false for IfElse', () => {
    const node = { runtimeType: 'IfElse' } as NodeDto;
    expect(isSwitchNode(node)).toBe(false);
  });

  it('isIfElseNode returns true for IfElse', () => {
    const node = { runtimeType: 'IfElse' } as NodeDto;
    expect(isIfElseNode(node)).toBe(true);
  });

  it('isForEachNode returns true for ForEach', () => {
    const node = { runtimeType: 'ForEach' } as NodeDto;
    expect(isForEachNode(node)).toBe(true);
  });

  it('isWhileNode returns true for While', () => {
    const node = { runtimeType: 'While' } as NodeDto;
    expect(isWhileNode(node)).toBe(true);
  });

  it('isSubflowNode returns true for Subflow', () => {
    const node = { runtimeType: 'Subflow' } as NodeDto;
    expect(isSubflowNode(node)).toBe(true);
  });
});
