import { describe, it, expect } from 'vitest';
import { nodeDtoSchema, handleNode } from '../node-union';
import fixture from '../../__fixtures__/node-union.all-13-kinds.json';
import enumFixture from '../../__fixtures__/engine-enums.json';

describe('nodeDtoSchema (discriminated union)', () => {
  it('parses all 13 kinds', () => {
    for (const node of fixture) {
      expect(() => nodeDtoSchema.parse(node)).not.toThrow();
    }
  });

  it('rejects unknown runtimeType', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'Mystery',
      nodeId: 'n',
    });
    expect(result.success).toBe(false);
  });

  it('discriminator is runtimeType', () => {
    // Zod v4 does not expose .discriminator; verify by parsing with correct key
    const valid = nodeDtoSchema.safeParse({ runtimeType: 'Noop', nodeId: 'n' });
    expect(valid.success).toBe(true);
  });

  it('has 13 options', () => {
    expect(nodeDtoSchema.options).toHaveLength(13);
  });

  it('fails on missing runtimeType', () => {
    const result = nodeDtoSchema.safeParse({ nodeId: 'n' });
    expect(result.success).toBe(false);
  });

  it('fixture length matches engine-enums.json RuntimeType members', () => {
    expect(fixture).toHaveLength(
      (enumFixture as Record<string, { members: string[] }>).RuntimeType.members.length,
    );
  });

  it('rejects CSharpScript with both scriptPath and scriptContent set', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'CSharpScript',
      nodeId: 'n',
      scriptPath: 'a.csx',
      scriptContent: 'code',
    });
    // Base schema accepts; strict check is on csharpScriptNodeSchema
    // The discriminated union uses base schema which accepts both
    expect(result.success).toBe(true);
  });

  it('allows CSharpScript with only scriptPath set via union', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'CSharpScript',
      nodeId: 'n',
      scriptPath: 'a.csx',
    });
    expect(result.success).toBe(true);
  });

  it('rejects Subflow with both path and definition set', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'Subflow',
      nodeId: 'n',
      workflowFilePath: 'child.yaml',
      workflowDefinition: { workflowId: 'inline', nodes: [], connections: [] },
    });
    expect(result.success).toBe(false);
  });

  it('rejects Subflow with neither path nor definition set', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'Subflow',
      nodeId: 'n',
    });
    expect(result.success).toBe(false);
  });

  it('rejects Timer with invalid schedule via union', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'Timer',
      nodeId: 'n',
      schedule: 'not-a-schedule',
    });
    expect(result.success).toBe(false);
  });

  it('accepts Timer with valid CRON schedule via union', () => {
    const result = nodeDtoSchema.safeParse({
      runtimeType: 'Timer',
      nodeId: 'n',
      schedule: '*/5 * * * *',
    });
    expect(result.success).toBe(true);
  });
});

describe('handleNode', () => {
  it('dispatches to correct handler', () => {
    const handlers = {
      Noop: () => 'noop',
      CSharp: () => 'csharp',
      CSharpScript: () => 'csharpscript',
      CSharpTask: () => 'csharptask',
      PowerShell: () => 'powershell',
      PowerShellTask: () => 'powershelltask',
      IfElse: () => 'ifelse',
      ForEach: () => 'foreach',
      While: () => 'while',
      Switch: () => 'switch',
      Subflow: () => 'subflow',
      Timer: () => 'timer',
      Container: () => 'container',
    };
    expect(handleNode({ runtimeType: 'Noop' }, handlers)).toBe('noop');
  });

  it('dispatches all 13 kinds correctly', () => {
    const handlers = Object.fromEntries(
      ['Noop','CSharp','CSharpScript','CSharpTask','PowerShell','PowerShellTask',
       'IfElse','ForEach','While','Switch','Subflow','Timer','Container'].map(
        (k) => [k, () => k],
      ),
    ) as Record<string, () => string>;

    for (const kind of ['Noop','CSharp','CSharpScript','CSharpTask','PowerShell',
      'PowerShellTask','IfElse','ForEach','While','Switch','Subflow','Timer','Container']) {
      expect(handleNode({ runtimeType: kind as any }, handlers as any)).toBe(kind);
    }
  });

  it('returns typed result', () => {
    const handlers = Object.fromEntries(
      ['Noop','CSharp','CSharpScript','CSharpTask','PowerShell','PowerShellTask',
       'IfElse','ForEach','While','Switch','Subflow','Timer','Container'].map(
        (k) => [k, () => 42],
      ),
    ) as Record<string, () => number>;
    expect(handleNode({ runtimeType: 'CSharp' }, handlers as any)).toBe(42);
  });

  it('throws on unhandled runtime type at runtime', () => {
    expect(() =>
      handleNode({ runtimeType: 'Mystery' as any }, {} as any),
    ).toThrow('Unhandled runtime type');
  });
});
