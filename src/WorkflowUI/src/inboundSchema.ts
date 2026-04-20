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
  ],
  HttpRequest: [
    { name: 'statusCode', type: 'number' },
    { name: 'responseBody', type: 'string' },
    { name: 'responseHeaders', type: 'object' },
  ],
  AI: [
    { name: 'result', type: 'string' },
    { name: 'model', type: 'string' },
    { name: 'tokensUsed', type: 'number' },
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

/**
 * Discover all inbound fields available to a node by tracing its incoming edges.
 */
export function discoverInboundFields(nodeId: string): InboundField[] {
  const fields: InboundField[] = [];
  const seen = new Set<string>();

  // Find all edges targeting this node
  State.edges.forEach(edge => {
    if (edge.targetNodeId !== nodeId) return;

    const sourceNode = State.nodes.get(edge.sourceNodeId);
    if (!sourceNode) return;
    const sourceName = sourceNode.nodeName || sourceNode.nodeId;

    if (edge.sourcePort === 'error') {
      // Error port → NodeFailMessage fields
      for (const f of errorMessageFields) {
        if (!seen.has(f.path)) {
          seen.add(f.path);
          fields.push({ ...f, sourceNode: sourceName });
        }
      }
    } else {
      // Normal output → node's output data fields
      const runtimeType = sourceNode.runtimeType;
      const schema = nodeOutputSchemas[runtimeType];
      if (schema) {
        for (const f of schema) {
          const path = f.name;
          if (!seen.has(path)) {
            seen.add(path);
            fields.push({
              path,
              name: f.name,
              type: f.type,
              source: 'output',
              sourceNode: sourceName,
              sourcePort: edge.sourcePort || 'out',
            });
          }
        }
      }

      // Also add any config-defined output keys from the source node
      if (sourceNode.config) {
        const outputKey = sourceNode.config.outputKey;
        if (outputKey && typeof outputKey === 'string') {
          const path = String(outputKey);
          if (!seen.has(path)) {
            seen.add(path);
            fields.push({
              path,
              name: String(outputKey),
              type: 'unknown',
              source: 'output',
              sourceNode: sourceName,
              sourcePort: edge.sourcePort || 'out',
            });
          }
        }
      }
    }
  });

  // Add universal context fields
  const contextFields: Omit<InboundField, 'sourceNode' | 'sourcePort'>[] = [
    { path: 'global.__workflowId', name: '__workflowId', type: 'string', source: 'context' },
    { path: 'global.__instanceId', name: '__instanceId', type: 'guid', source: 'context' },
  ];
  for (const f of contextFields) {
    if (!seen.has(f.path)) {
      seen.add(f.path);
      fields.push({ ...f, sourceNode: 'workflow', sourcePort: '' });
    }
  }

  return fields;
}
