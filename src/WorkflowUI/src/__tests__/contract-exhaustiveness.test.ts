import { describe, it, expect } from 'vitest';
import type { NodeDto } from '../engine-contract';
import { assertNever, RUNTIME_TYPES } from '../engine-contract';

/**
 * Exhaustive handler that covers all 13 runtime types.
 * If a new RuntimeType is added to the engine without updating this function,
 * TypeScript will produce a compile error on the assertNever call.
 */
function exhaustiveHandler(node: NodeDto): string {
  switch (node.runtimeType) {
    case 'Noop':
      return 'noop';
    case 'CSharp':
      return 'csharp';
    case 'CSharpScript':
      return 'csharp-script';
    case 'CSharpTask':
      return 'csharp-task';
    case 'PowerShell':
      return 'powershell';
    case 'PowerShellTask':
      return 'powershell-task';
    case 'IfElse':
      return 'if-else';
    case 'ForEach':
      return 'for-each';
    case 'While':
      return 'while';
    case 'Switch':
      return 'switch';
    case 'Subflow':
      return 'subflow';
    case 'Timer':
      return 'timer';
    case 'Container':
      return 'container';
    default:
      return assertNever(node);
  }
}

describe('Contract facade exhaustiveness', () => {
  it('exhaustiveHandler compiles and handles all 13 kinds', () => {
    // This test proves the switch compiles with all 13 cases + assertNever default.
    // If it compiles, TypeScript verified exhaustiveness.
    expect(RUNTIME_TYPES).toHaveLength(13);

    // Exercise each case at runtime
    for (const rt of RUNTIME_TYPES) {
      const node = { runtimeType: rt } as NodeDto;
      const result = exhaustiveHandler(node);
      expect(typeof result).toBe('string');
      expect(result.length).toBeGreaterThan(0);
    }
  });

  it('assertNever throws for unknown runtimeType at runtime', () => {
    const unknown = { runtimeType: 'UnknownKind' } as unknown as NodeDto;
    expect(() => exhaustiveHandler(unknown)).toThrow(/UnknownKind/);
  });
});
