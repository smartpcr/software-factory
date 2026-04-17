import type { VisibilityRule } from './visibilityRules';

function hasValue(values: Record<string, unknown>, key: string): boolean {
  const val = values[key];
  return typeof val === 'string' && val.trim().length > 0;
}

/**
 * CSharpTask visibility rules:
 * - Script mode: when scriptContent has value, hide executorTypeName and executorAssemblyPath
 * - Executor mode: when executorTypeName has value, hide scriptContent
 */
export const csharpTaskRules: VisibilityRule[] = [
  {
    field: 'scriptContent',
    visibleWhen: (values) => !hasValue(values, 'executorTypeName'),
  },
  {
    field: 'executorTypeName',
    visibleWhen: (values) => !hasValue(values, 'scriptContent'),
  },
  {
    field: 'executorAssemblyPath',
    visibleWhen: (values) => !hasValue(values, 'scriptContent'),
  },
];
