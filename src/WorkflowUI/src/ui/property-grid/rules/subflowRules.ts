import type { VisibilityRule } from './visibilityRules';

function hasValue(values: Record<string, unknown>, key: string): boolean {
  const val = values[key];
  return val != null && val !== '';
}

/**
 * Subflow visibility rules:
 * - When workflowFilePath has value, hide workflowDefinition
 * - When workflowDefinition has value, hide workflowFilePath
 */
export const subflowRules: VisibilityRule[] = [
  {
    field: 'workflowFilePath',
    visibleWhen: (values) => !hasValue(values, 'workflowDefinition'),
  },
  {
    field: 'workflowDefinition',
    visibleWhen: (values) => !hasValue(values, 'workflowFilePath'),
  },
];
