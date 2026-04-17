import { describe, it, expect } from 'vitest';
import { noopNodeSchema } from '../node-noop.schema';
import { csharpNodeSchema } from '../node-csharp.schema';
import {
  csharpScriptNodeSchema,
  csharpScriptNodeSchemaBase,
} from '../node-csharp-script.schema';
import { csharpTaskNodeSchema } from '../node-csharp-task.schema';
import { powershellNodeSchema } from '../node-powershell.schema';
import { powershellTaskNodeSchema } from '../node-powershell-task.schema';

describe('Task-family kind schemas', () => {
  describe('noopNodeSchema', () => {
    it('parses minimal node', () => {
      const result = noopNodeSchema.parse({ runtimeType: 'Noop', nodeId: 'n1' });
      expect(result.runtimeType).toBe('Noop');
    });

    it('rejects extra property (strict)', () => {
      const result = noopNodeSchema.safeParse({ runtimeType: 'Noop', nodeId: 'n1', bogus: 1 });
      expect(result.success).toBe(false);
    });

    it('has 12 keys in shape', () => {
      expect(Object.keys(noopNodeSchema.shape)).toHaveLength(12);
    });

    it('rejects wrong runtimeType', () => {
      const result = noopNodeSchema.safeParse({ runtimeType: 'CSharp', nodeId: 'n1' });
      expect(result.success).toBe(false);
    });
  });

  describe('csharpNodeSchema', () => {
    it('accepts null assemblyPath', () => {
      const result = csharpNodeSchema.parse({ runtimeType: 'CSharp', nodeId: 'n1', assemblyPath: null, typeName: null });
      expect(result.assemblyPath).toBeNull();
    });

    it('accepts absent assemblyPath', () => {
      const result = csharpNodeSchema.parse({ runtimeType: 'CSharp', nodeId: 'n1' });
      expect(result.assemblyPath).toBeUndefined();
    });

    it('rejects non-string assemblyPath', () => {
      const result = csharpNodeSchema.safeParse({ runtimeType: 'CSharp', nodeId: 'n1', assemblyPath: 42 });
      expect(result.success).toBe(false);
    });

    it('rejects extra keys (strict)', () => {
      const result = csharpNodeSchema.safeParse({ runtimeType: 'CSharp', nodeId: 'n1', unknownKey: 'x' });
      expect(result.success).toBe(false);
    });
  });

  describe('csharpScriptNodeSchema', () => {
    it('defaults scriptPath and scriptContent (base/draft)', () => {
      const result = csharpScriptNodeSchemaBase.parse({ runtimeType: 'CSharpScript', nodeId: 'n1' });
      expect(result.scriptPath).toBe('');
      expect(result.scriptContent).toBe('');
    });

    it('rejects extra keys (strict)', () => {
      const result = csharpScriptNodeSchemaBase.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1', extra: true });
      expect(result.success).toBe(false);
    });

    it('accepts inline script only', () => {
      const result = csharpScriptNodeSchema.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1', scriptContent: 'Console.WriteLine("hello");' });
      expect(result.success).toBe(true);
    });

    it('accepts file path only', () => {
      const result = csharpScriptNodeSchema.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1', scriptPath: 'scripts/task.csx' });
      expect(result.success).toBe(true);
    });

    it('rejects both scriptPath and scriptContent set', () => {
      const result = csharpScriptNodeSchema.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1', scriptPath: 'scripts/task.csx', scriptContent: 'Console.WriteLine("hello");' });
      expect(result.success).toBe(false);
      if (!result.success) {
        expect(result.error.issues.some((i) => i.message.includes('Exactly one'))).toBe(true);
      }
    });

    it('rejects both empty (strict mode)', () => {
      const result = csharpScriptNodeSchema.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1' });
      expect(result.success).toBe(false);
      if (!result.success) {
        expect(result.error.issues.some((i) => i.message.includes('ScriptPath or ScriptContent must be set'))).toBe(true);
      }
    });

    it('scriptPath not string fails', () => {
      const result = csharpScriptNodeSchema.safeParse({ runtimeType: 'CSharpScript', nodeId: 'n1', scriptPath: 42 });
      expect(result.success).toBe(false);
    });
  });

  describe('csharpTaskNodeSchema', () => {
    it('accepts all nullable properties', () => {
      const result = csharpTaskNodeSchema.parse({ runtimeType: 'CSharpTask', nodeId: 'n1', scriptContent: null, executorTypeName: null, executorAssemblyPath: null });
      expect(result.scriptContent).toBeNull();
    });

    it('rejects snake_case (executor_type_name) via strict', () => {
      const result = csharpTaskNodeSchema.safeParse({ runtimeType: 'CSharpTask', nodeId: 'n1', executor_type_name: 'bad' });
      expect(result.success).toBe(false);
    });
  });

  describe('powershellNodeSchema', () => {
    it('defaults scriptPath to empty string', () => {
      const result = powershellNodeSchema.parse({ runtimeType: 'PowerShell', nodeId: 'n1' });
      expect(result.scriptPath).toBe('');
    });

    it('accepts array of strings for requiredModules', () => {
      const result = powershellNodeSchema.parse({ runtimeType: 'PowerShell', nodeId: 'n1', requiredModules: ['Az', 'Pester'] });
      expect(result.requiredModules).toEqual(['Az', 'Pester']);
    });

    it('rejects script_path (snake_case) via strict', () => {
      const result = powershellNodeSchema.safeParse({ runtimeType: 'PowerShell', nodeId: 'n1', script_path: 'bad.ps1' });
      expect(result.success).toBe(false);
    });
  });

  describe('powershellTaskNodeSchema', () => {
    it('accepts nullable scriptPath', () => {
      const result = powershellTaskNodeSchema.parse({ runtimeType: 'PowerShellTask', nodeId: 'n1', scriptPath: null });
      expect(result.scriptPath).toBeNull();
    });

    it('rejects extra keys (strict)', () => {
      const result = powershellTaskNodeSchema.safeParse({ runtimeType: 'PowerShellTask', nodeId: 'n1', extraProp: 'x' });
      expect(result.success).toBe(false);
    });
  });
});
