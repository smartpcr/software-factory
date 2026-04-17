import { useState, useCallback } from 'react';
import { useGraphStore } from '../state/graphStore';
import { getSchemaForRuntimeType } from '../property-grid/schemaRegistry';
import type { ValidationEntry } from './ValidationPanel';
import type { RuntimeType } from '../../engine-contract';
import { RUNTIME_TYPES } from '../../engine-contract';

export interface UseWorkflowValidationResult {
  entries: ValidationEntry[];
  validate: () => Promise<void>;
  isValidating: boolean;
}

function isValidRuntimeType(rt: unknown): rt is RuntimeType {
  return typeof rt === 'string' && (RUNTIME_TYPES as readonly string[]).includes(rt);
}

/** Host validation response shape matching the server /api/workflows/validate endpoint */
export interface HostValidationResult {
  nodeId: string;
  field?: string;
  message: string;
  severity: 'error' | 'warning';
}

/**
 * Attempts host-backed workflow validation via /api/workflows/validate.
 * Falls back to client-only if the host is unavailable.
 */
async function validateViaHost(workflowId: string): Promise<HostValidationResult[] | null> {
  try {
    const response = await fetch('/api/workflows/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ workflowId }),
    });
    if (!response.ok) return null;
    const data: unknown = await response.json();
    if (Array.isArray(data)) {
      return data as HostValidationResult[];
    }
    return null;
  } catch {
    return null;
  }
}

export function useWorkflowValidation(): UseWorkflowValidationResult {
  const [entries, setEntries] = useState<ValidationEntry[]>([]);
  const [isValidating, setIsValidating] = useState(false);

  const validate = useCallback(async () => {
    setIsValidating(true);
    const { nodes, workflowId } = useGraphStore.getState();

    // Try host-backed validation first
    const hostResults = await validateViaHost(workflowId);
    if (hostResults !== null) {
      setEntries(hostResults);
      setIsValidating(false);
      return;
    }

    // Fallback: client-side schema validation
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
