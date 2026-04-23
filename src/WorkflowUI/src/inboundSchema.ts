// -----------------------------------------------------------------------
// Inbound schema discovery — traces edges to determine what data
// flows into a node, so parameter bindings can auto-suggest fields.
// -----------------------------------------------------------------------

import { State } from './state';
import type { FlowNode, FlowEdge } from './types';

export interface InboundField {
  /** Full path for binding expression, e.g. "input.errorMessage" */
  path: string;
  /** Short display name */
  name: string;
  /** Data type hint */
  type: 'string' | 'number' | 'boolean' | 'object' | 'datetime' | 'guid' | 'unknown';
  /** Where this field comes from */
  source: 'error' | 'output' | 'context';
  /** Source node name for display */
  sourceNode: string;
  /** Source port type */
  sourcePort: string;
}

/** Known fields from NodeFailMessage (routed via error port) */
const errorMessageFields: Omit<InboundField, 'sourceNode'>[] = [
  { path: 'errorMessage', name: 'errorMessage', type: 'string', source: 'error', sourcePort: 'error' },
  { path: 'nodeId', name: 'nodeId', type: 'string', source: 'error', sourcePort: 'error' },
  { path: 'timestamp', name: 'timestamp', type: 'datetime', source: 'error', sourcePort: 'error' },
  { path: 'nodeInstanceId', name: 'nodeInstanceId', type: 'guid', source: 'error', sourcePort: 'error' },
  { path: 'exception', name: 'exception', type: 'string', source: 'error', sourcePort: 'error' },
  { path: 'exceptionType', name: 'exceptionType', type: 'string', source: 'error', sourcePort: 'error' },
  { path: 'stackTrace', name: 'stackTrace', type: 'string', source: 'error', sourcePort: 'error' },
];

/** Known output fields by node runtime type */
const nodeOutputSchemas: Record<string, { name: string; type: InboundField['type'] }[]> = {
  Start: [
    { name: 'triggerType', type: 'string' },
    { name: 'triggerTime', type: 'datetime' },
    { name: 'schedule', type: 'string' },
    { name: 'eventName', type: 'string' },
    { name: 'webHookMethod', type: 'string' },
    { name: 'webHookRoute', type: 'string' },
    { name: 'messageType', type: 'string' },
  ],
  HttpRequest: [
    { name: 'statusCode', type: 'number' },
    { name: 'responseBody', type: 'string' },
    { name: 'responseHeaders', type: 'object' },
  ],
  AI: [
    { name: 'result', type: 'string' },
    { name: 'mode', type: 'string' },
    { name: 'backend', type: 'string' },
    { name: 'model', type: 'string' },
    { name: 'agentName', type: 'string' },
    { name: 'agentSummary', type: 'string' },
    { name: 'ai.success', type: 'boolean' },
    { name: 'ai.model', type: 'string' },
    { name: 'ai.durationMs', type: 'number' },
    { name: 'ai.inputTokens', type: 'number' },
    { name: 'ai.outputTokens', type: 'number' },
  ],
  Timer: [
    { name: 'triggerTime', type: 'datetime' },
    { name: 'nextTriggerTime', type: 'datetime' },
  ],
  Transform: [
    { name: 'result', type: 'object' },
  ],
  Log: [
    { name: 'level', type: 'string' },
    { name: 'message', type: 'string' },
  ],
  Delay: [
    { name: 'duration', type: 'number' },
  ],
  IfElse: [
    { name: 'conditionResult', type: 'boolean' },
  ],
  Switch: [
    { name: 'matchedCase', type: 'string' },
  ],
  ForEach: [
    { name: 'currentItem', type: 'object' },
    { name: 'currentIndex', type: 'number' },
  ],
};

/** Map a JSON Schema type string to an InboundField type */
function jsonSchemaTypeToFieldType(
  schemaType?: string,
  format?: string,
): InboundField['type'] {
  if (format === 'date-time' || format === 'date') return 'datetime';
  if (format === 'int64' || format === 'int32') return 'number';
  switch (schemaType) {
    case 'integer':
    case 'number': return 'number';
    case 'boolean': return 'boolean';
    case 'string': return 'string';
    case 'object': return 'object';
    default: return 'unknown';
  }
}

/**
 * Parse a JSON Schema's "properties" into InboundField descriptors.
 * Supports nested objects (flattened with dot-path).
 */
function parseJsonSchemaProperties(
  schema: string,
  prefix = 'body',
): { name: string; path: string; type: InboundField['type'] }[] {
  const results: { name: string; path: string; type: InboundField['type'] }[] = [];
  try {
    const parsed = JSON.parse(schema);
    const props = parsed?.properties;
    if (!props || typeof props !== 'object') return results;

    for (const [key, def] of Object.entries(props)) {
      const d = def as { type?: string; format?: string; properties?: object };
      const path = `${prefix}.${key}`;
      const fieldType = jsonSchemaTypeToFieldType(d.type, d.format);
      results.push({ name: key, path, type: fieldType });

      // Recurse into nested objects
      if (d.type === 'object' && d.properties) {
        const nested = parseJsonSchemaProperties(JSON.stringify({ properties: d.properties }), path);
        results.push(...nested);
      }
    }
  } catch {
    // Invalid JSON — skip
  }
  return results;
}

/**
 * Discover all inbound fields available to a node by tracing its incoming edges.
 * Fields use bare names by default; prefixes are added only when names conflict
 * across sources (e.g. an output field shares a name with a global variable,
 * or multiple upstream nodes produce a field with the same name).
 */
export function discoverInboundFields(nodeId: string): InboundField[] {
  // Phase 1: Collect all candidate fields with their canonical prefixed path
  interface Candidate {
    bareName: string;
    prefixedPath: string;
    type: InboundField['type'];
    source: InboundField['source'];
    sourceNode: string;
    sourcePort: string;
  }
  const candidates: Candidate[] = [];

  State.edges.forEach(edge => {
    if (edge.targetNodeId !== nodeId) return;

    const sourceNode = State.nodes.get(edge.sourceNodeId);
    if (!sourceNode) return;
    const sourceName = sourceNode.nodeName || sourceNode.nodeId;

    if (edge.sourcePort === 'error') {
      for (const f of errorMessageFields) {
        candidates.push({
          bareName: f.name,
          prefixedPath: `error.${f.name}`,
          type: f.type,
          source: 'error',
          sourceNode: sourceName,
          sourcePort: 'error',
        });
      }
    } else {
      const runtimeType = sourceNode.runtimeType;
      const schema = nodeOutputSchemas[runtimeType];
      if (schema) {
        for (const f of schema) {
          candidates.push({
            bareName: f.name,
            prefixedPath: `output.${f.name}`,
            type: f.type,
            source: 'output',
            sourceNode: sourceName,
            sourcePort: edge.sourcePort || 'out',
          });
        }
      }

      // Start node with WebHook trigger → payload fields (body.X are literal keys)
      if (runtimeType.toLowerCase() === 'start' && sourceNode.config) {
        const trigType = String(sourceNode.config.triggerType || '');
        if (trigType === 'WebHook' && sourceNode.config.bodyJsonSchema) {
          const payloadFields = parseJsonSchemaProperties(
            String(sourceNode.config.bodyJsonSchema),
            'body',
          );
          for (const pf of payloadFields) {
            candidates.push({
              bareName: pf.name,
              prefixedPath: pf.path,  // "body.fieldName" — literal key in InputData
              type: pf.type,
              source: 'output',
              sourceNode: sourceName,
              sourcePort: edge.sourcePort || 'out',
            });
          }
        }
      }

      // Config-defined output key (skip for AI nodes with declared outputs)
      if (sourceNode.config) {
        const isAI = runtimeType.toLowerCase() === 'ai';
        const outputKey = sourceNode.config.outputKey;

        // AI nodes: skill/agent declared output fields (scenario-aware)
        let aiOutputs: { name: string; type: string; description: string }[] | undefined;
        if (isAI && (window as unknown as Record<string, unknown>).__cachedAIOutputs) {
          const mode = String(sourceNode.config.mode || 'skill').toLowerCase();
          const name = mode === 'skill' ? String(sourceNode.config.skillName || '') : String(sourceNode.config.subAgentName || '');
          const key = `${mode}:${name}`;
          const outputsMap = (window as unknown as Record<string, unknown>).__cachedAIOutputs as Record<string, { name: string; type: string; description: string }[]>;
          aiOutputs = outputsMap[key];
          if (aiOutputs) {
            for (const ao of aiOutputs) {
              const fieldType: InboundField['type'] = ao.type === 'file' || ao.type === 'folder' ? 'string' : (ao.type as InboundField['type']) || 'unknown';
              candidates.push({
                bareName: ao.name,
                prefixedPath: `output.${ao.name}`,
                type: fieldType,
                source: 'output',
                sourceNode: sourceName,
                sourcePort: edge.sourcePort || 'out',
              });
            }
          }
        }

        // Only add generic outputKey if no AI declared outputs exist
        if (outputKey && typeof outputKey === 'string' && !(isAI && aiOutputs && aiOutputs.length > 0)) {
          candidates.push({
            bareName: String(outputKey),
            prefixedPath: `output.${outputKey}`,
            type: 'unknown',
            source: 'output',
            sourceNode: sourceName,
            sourcePort: edge.sourcePort || 'out',
          });
        }
      }
    }
  });

  // Add global/context fields
  const wf = State.workflows[State.currentWfId || ''];
  candidates.push(
    { bareName: '__workflowId', prefixedPath: 'global.__workflowId', type: 'string', source: 'context', sourceNode: 'workflow', sourcePort: '' },
    { bareName: '__instanceId', prefixedPath: 'global.__instanceId', type: 'guid', source: 'context', sourceNode: 'workflow', sourcePort: '' },
  );
  if (wf?.defaultVariables) {
    for (const [key, val] of Object.entries(wf.defaultVariables)) {
      let varType: InboundField['type'] = 'unknown';
      switch (typeof val) {
        case 'string': varType = 'string'; break;
        case 'number': varType = 'number'; break;
        case 'boolean': varType = 'boolean'; break;
        case 'object': varType = 'object'; break;
      }
      candidates.push({
        bareName: key,
        prefixedPath: `global.${key}`,
        type: varType,
        source: 'context',
        sourceNode: 'workflow',
        sourcePort: '',
      });
    }
  }

  // Add scoped variables from enclosing Group nodes
  let current = State.nodes.get(nodeId);
  const visited = new Set<string>();
  while (current?.parentId && !visited.has(current.parentId)) {
    visited.add(current.parentId);
    const parent = State.nodes.get(current.parentId);
    if (parent && parent.runtimeType.toLowerCase() === 'group' && parent.config?.scopedVariables) {
      let scopedVars: { name: string; type: string; default?: string }[] = [];
      try {
        scopedVars = typeof parent.config.scopedVariables === 'string'
          ? JSON.parse(parent.config.scopedVariables as string)
          : Array.isArray(parent.config.scopedVariables) ? parent.config.scopedVariables as { name: string; type: string }[] : [];
      } catch { /* ignore */ }
      const groupName = parent.nodeName || parent.nodeId;
      for (const v of scopedVars) {
        if (!v.name) continue;
        candidates.push({
          bareName: v.name,
          prefixedPath: `scope.${v.name}`,
          type: (v.type as InboundField['type']) || 'unknown',
          source: 'context',
          sourceNode: groupName,
          sourcePort: '',
        });
      }
    }
    current = parent || undefined;
  }

  // Deduplicate candidates by prefixedPath (same source + same field = one entry)
  const seenPaths = new Set<string>();
  const dedupedCandidates: Candidate[] = [];
  for (const c of candidates) {
    const key = `${c.sourceNode}:${c.prefixedPath}`;
    if (!seenPaths.has(key)) {
      seenPaths.add(key);
      dedupedCandidates.push(c);
    }
  }

  // Phase 2: Detect conflicts — a bare name conflicts when used by multiple sources
  const bareNameSources = new Map<string, Set<string>>();
  for (const c of dedupedCandidates) {
    if (!bareNameSources.has(c.bareName)) bareNameSources.set(c.bareName, new Set());
    bareNameSources.get(c.bareName)!.add(c.source === 'context' ? 'global' : c.source);
  }

  // Phase 3: Build final fields — bare name when unique, prefixed when conflicting
  const fields: InboundField[] = [];
  const seen = new Set<string>();

  for (const c of dedupedCandidates) {
    const sources = bareNameSources.get(c.bareName)!;
    const hasConflict = sources.size > 1;

    // body.* fields always use their literal prefixed path (it's the actual InputData key)
    const isBodyField = c.prefixedPath.startsWith('body.');
    const path = (hasConflict || isBodyField) ? c.prefixedPath : c.bareName;

    if (seen.has(path)) continue;
    seen.add(path);

    fields.push({
      path,
      name: c.bareName,
      type: c.type,
      source: c.source,
      sourceNode: c.sourceNode,
      sourcePort: c.sourcePort,
    });

    // When conflicting, also add the bare name so both are available for autocomplete
    if (hasConflict && !isBodyField && !seen.has(c.bareName)) {
      seen.add(c.bareName);
      fields.push({
        path: c.bareName,
        name: c.bareName,
        type: c.type,
        source: c.source,
        sourceNode: c.sourceNode,
        sourcePort: c.sourcePort,
      });
    }
  }

  return fields;
}
