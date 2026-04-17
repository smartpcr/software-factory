import { describe, it, expect } from 'vitest';
import { evaluateVisibilityRules } from '../../property-grid/rules/visibilityRules';
import { csharpTaskRules } from '../../property-grid/rules/csharpTaskRules';
import { subflowRules } from '../../property-grid/rules/subflowRules';

describe('visibilityRules', () => {
  describe('evaluateVisibilityRules', () => {
    it('returns correct visible set for matching rules', () => {
      const rules = [
        { field: 'a', visibleWhen: () => true },
        { field: 'b', visibleWhen: () => false },
        { field: 'c', visibleWhen: () => true },
      ];

      const visible = evaluateVisibilityRules(rules, {});
      expect(visible.has('a')).toBe(true);
      expect(visible.has('b')).toBe(false);
      expect(visible.has('c')).toBe(true);
      expect(visible.size).toBe(2);
    });
  });

  describe('csharpTaskRules', () => {
    it('script mode hides executor fields', () => {
      const values = {
        scriptContent: 'Console.WriteLine("Hello");',
        executorTypeName: '',
        executorAssemblyPath: '',
      };

      const visible = evaluateVisibilityRules(csharpTaskRules, values);
      expect(visible.has('scriptContent')).toBe(true);
      expect(visible.has('executorTypeName')).toBe(false);
      expect(visible.has('executorAssemblyPath')).toBe(false);
    });

    it('executor mode hides script field', () => {
      const values = {
        scriptContent: '',
        executorTypeName: 'MyExecutor',
        executorAssemblyPath: '/path/to/assembly.dll',
      };

      const visible = evaluateVisibilityRules(csharpTaskRules, values);
      expect(visible.has('scriptContent')).toBe(false);
      expect(visible.has('executorTypeName')).toBe(true);
      expect(visible.has('executorAssemblyPath')).toBe(true);
    });
  });

  describe('subflowRules', () => {
    it('file path mode hides inline definition', () => {
      const values = {
        workflowFilePath: './workflows/sub.yaml',
        workflowDefinition: null,
      };

      const visible = evaluateVisibilityRules(subflowRules, values);
      expect(visible.has('workflowFilePath')).toBe(true);
      expect(visible.has('workflowDefinition')).toBe(false);
    });

    it('inline mode hides file path', () => {
      const values = {
        workflowFilePath: '',
        workflowDefinition: { workflowId: 'inline-1' },
      };

      const visible = evaluateVisibilityRules(subflowRules, values);
      expect(visible.has('workflowFilePath')).toBe(false);
      expect(visible.has('workflowDefinition')).toBe(true);
    });
  });
});
