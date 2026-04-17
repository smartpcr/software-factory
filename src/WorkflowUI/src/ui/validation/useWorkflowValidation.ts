import { useState, useCallback } from 'react';
import { useGraphStore } from '../state/graphStore';
import { getSchemaForRuntimeType } from '../property-grid/schemaRegistry';
import type { ValidationEntry } from './ValidationPanel';
import type { RuntimeType } from '../../engine-contract';
import { RUNTIME_TYPES } from '../../engine-contract';

export interface UseWorkflowValidationResult {
  entries: ValidationEntry[];
  validate: () => void;
  isValidating: boolean;
}

function isValidRuntimeType(rt: unknown): rt is RuntimeType {
  return typeof rt === 'string' && (RUNTIME_TYPES as readonly string[]).includes(rt);
}

export function useWorkflowValidation(): UseWorkflowValidationResult {
  const [entries, setEntries] = useState<ValidationEntry[]>([]);
  const [isValidating, setIsValidating] = useState(false);

  const validate = useCallback(() => {
    setIsValidating(true);
    const nodes = useGraphStore.getState().nodes;
    const results: ValidationEntry[] = [];

    for (const node of nodes) {
      if (!isValidRuntimeType(node.runtimeType)) {
        results.push({
          nodeId: node.nodeId,
          message: `Unknown runtime type: ${String(node.runtimeType)}`,
          severity: 'error',
        });
        continue;
      }

      const schema = getSchemaForRuntimeType(node.runtimeType);
      const parsed = schema.safeParse(node);

      if (!parsed.success) {
        for (const issue of parsed.error.issues) {
          results.push({
            nodeId: node.nodeId,
            field: issue.path.map(String).join('.'),
            message: issue.message,
            severity: 'error',
          });
        }
      }
    }

    setEntries(results);
    setIsValidating(false);
  }, []);

  return { entries, validate, isValidating };
}
