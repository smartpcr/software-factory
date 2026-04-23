// -----------------------------------------------------------------------
// Server API client
// -----------------------------------------------------------------------

import type { WorkflowData, FlowNode } from './types';

function encodePath(p: string): string {
  return p.split('/').map(seg => encodeURIComponent(seg)).join('/');
}

const API_BASE = '/api/workflows';
const FILES_BASE = '/api/files';

// ---- Workflow CRUD ----

export async function listWorkflows(): Promise<unknown[]> {
  const res = await fetch(API_BASE);
  if (!res.ok) throw new Error(`GET ${API_BASE} failed: ${res.status}`);
  return res.json();
}

export async function getWorkflow(id: string): Promise<unknown> {
  const url = `${API_BASE}/${encodePath(id)}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status}`);
  return res.json();
}

export async function saveWorkflow(id: string, data: WorkflowData): Promise<void> {
  const url = `${API_BASE}/${encodePath(id)}`;
  // Map to engine WorkflowDefinition schema
  // Identify Group children (nodes with parentId pointing to a Group)
  const allNodes = data.nodes || [];
  const groupChildIds = new Set<string>();
  for (const n of allNodes) {
    if (n.parentId) {
      const parent = allNodes.find(p => p.nodeId === n.parentId);
      if (parent && parent.runtimeType.toLowerCase() === 'group') {
        groupChildIds.add(n.nodeId);
      }
    }
  }

  const mapNode = (n: FlowNode) => ({
    nodeId: n.nodeId,
    runtimeType: n.runtimeType,
    nodeName: n.nodeName,
    description: n.description || '',
    configuration: n.config || {},
    joinType: n.joinType || 'Any',
    priority: n.priority || 'Normal',
    maxConcurrentExecutions: n.maxConcurrentExecutions || 0,
    retryPolicy: n.retryPolicy || null,
    circuitBreakerPolicy: n.circuitBreakerPolicy || null,
    compensationNodeId: n.compensationNodeId || null,
    fallbackNodeId: n.fallbackNodeId || null,
  });

  const allEdges = data.edges || [];

  const topLevelNodes = allNodes.filter(n => !groupChildIds.has(n.nodeId)).map(n => {
    const mapped = mapNode(n);
    // Embed children inside Group nodes
    if (n.runtimeType.toLowerCase() === 'group') {
      const children = allNodes.filter(c => c.parentId === n.nodeId);
      const childNodeIds = new Set(children.map(c => c.nodeId));
      // Only connections between children (not group↔child which are structural)
      const childConns = allEdges.filter(e => {
        const srcIsChild = childNodeIds.has(e.sourceNodeId);
        const tgtIsChild = childNodeIds.has(e.targetNodeId);
        return srcIsChild && tgtIsChild;
      });
      (mapped.configuration as Record<string, unknown>).childNodes = children.map(mapNode);
      (mapped.configuration as Record<string, unknown>).childConnections = childConns.map(e => ({
        sourceNodeId: e.sourceNodeId,
        targetNodeId: e.targetNodeId,
        sourcePort: e.sourcePortName || e.sourcePort || 'out',
        targetPort: e.targetPort || 'in',
        triggerMessageType: e.sourcePort === 'error' ? 'Fail' : (e.triggerMessageType || 'Complete'),
      }));
      // Remove engine-level defaults that are now embedded
      delete (mapped.configuration as Record<string, unknown>).executionMode;
    }
    return mapped;
  });

  // Filter out edges internal to groups:
  // - child↔child edges (embedded in childConnections)
  // - group↔child structural edges (_body port, feedback loops) — UI-only, not needed by engine
  const groupInternalEdgeIds = new Set<string>();
  for (const n of allNodes) {
    if (n.runtimeType.toLowerCase() !== 'group') continue;
    const childNodeIds = new Set(allNodes.filter(c => c.parentId === n.nodeId).map(c => c.nodeId));
    for (const e of allEdges) {
      const srcIsChild = childNodeIds.has(e.sourceNodeId);
      const tgtIsChild = childNodeIds.has(e.targetNodeId);
      const srcIsGroup = e.sourceNodeId === n.nodeId;
      const tgtIsGroup = e.targetNodeId === n.nodeId;
      // Exclude: child↔child, group→child (_body), child→group (feedback)
      if ((srcIsChild && tgtIsChild) || (srcIsGroup && tgtIsChild) || (srcIsChild && tgtIsGroup)) {
        groupInternalEdgeIds.add(e.id);
      }
    }
  }

  const workflowDef = {
    workflowId: data.workflowId,
    workflowName: data.workflowName,
    description: data.description || '',
    version: data.version || '1.0',
    entryPointNodeId: data.entryPointNodeId || null,
    maxConcurrency: data.maxConcurrency || 0,
    allowPause: data.allowPause !== false,
    timeoutSeconds: data.timeoutSeconds || 0,
    defaultVariables: data.defaultVariables || {},
    metadata: data.metadata || {},
    nodes: topLevelNodes,
    connections: allEdges.filter(e => !groupInternalEdgeIds.has(e.id)).map(e => {
      let msgType: string;
      if (e.sourcePort === 'error') {
        msgType = 'Fail';
      } else {
        msgType = e.triggerMessageType || 'Complete';
      }
      return {
        sourceNodeId: e.sourceNodeId,
        targetNodeId: e.targetNodeId,
        sourcePort: e.sourcePortName || e.sourcePort || 'out',
        targetPort: e.targetPort || 'in',
        triggerMessageType: msgType,
        condition: e.condition || null,
        isEnabled: e.isEnabled !== false,
        priority: e.priority || 0,
        metadata: e.metadata || null,
      };
    }),
  };
  const definition = JSON.stringify(workflowDef, null, 2);
  const sidecarJson = JSON.stringify({
    createdAt: data.createdAt,
    updatedAt: data.updatedAt,
    author: data.author,
    // UI layout data
    nodePositions: Object.fromEntries(
      (data.nodes || []).map(n => [n.nodeId, { x: n.x, y: n.y, width: n.width, height: n.height, parentId: n.parentId, parentSlot: n.parentSlot, collapsed: n.collapsed, branches: n.branches, children: n.children }])
    ),
    edgeLabels: Object.fromEntries(
      (data.edges || []).filter(e => e.label).map(e => [e.id, e.label])
    ),
  });
  const res = await fetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ definition, sidecarJson }),
  });
  if (!res.ok) {
    const errBody = await res.text().catch(() => '');
    throw new Error(`PUT ${url} failed: ${res.status} ${errBody}`);
  }
}

export async function deleteWorkflowApi(id: string): Promise<boolean> {
  const url = `${API_BASE}/${encodePath(id)}`;
  // First get etag
  const getRes = await fetch(url);
  if (!getRes.ok) return false;
  const record = await getRes.json();
  const etag = record?.etag || record?.eTag || '*';
  const res = await fetch(url, {
    method: 'DELETE',
    headers: { 'If-Match': etag },
  });
  return res.ok || res.status === 204;
}

// ---- File operations ----

export async function createWorkflowApi(id: string): Promise<void> {
  const res = await fetch(FILES_BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id }),
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE} failed: ${res.status}`);
}

export async function exportWorkflowsApi(ids: string[]): Promise<Blob> {
  const res = await fetch(`${FILES_BASE}/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids }),
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE}/export failed: ${res.status}`);
  return res.blob();
}

export async function importWorkflowsApi(zipBlob: Blob): Promise<{ imported: string[] }> {
  const res = await fetch(`${FILES_BASE}/import`, {
    method: 'POST',
    body: zipBlob,
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE}/import failed: ${res.status}`);
  return res.json() as Promise<{ imported: string[] }>;
}

// ---- Execution ----

export async function startRun(workflowId: string, testPayload?: string): Promise<{ runId: string }> {
  const body: Record<string, unknown> = { workflowId };
  if (testPayload) body.testPayload = testPayload;
  const res = await fetch('/api/runs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const errBody = await res.text().catch(() => '');
    throw new Error(`POST /api/runs failed: ${res.status} ${errBody}`);
  }
  return res.json() as Promise<{ runId: string }>;
}

export async function cancelRun(runId: string): Promise<void> {
  const res = await fetch(`/api/runs/${runId}/cancel`, {
    method: 'POST',
  });
  if (!res.ok) throw new Error(`POST cancel failed: ${res.status}`);
}

export interface RunStatus {
  runId: string;
  workflowId: string;
  workflowName: string;
  status: string;
  startTimeUtc: string;
  endTimeUtc?: string;
  error?: string;
}

export async function getRunStatus(runId: string): Promise<RunStatus> {
  const res = await fetch(`/api/runs/${runId}`);
  if (!res.ok) throw new Error(`GET run status failed: ${res.status}`);
  return res.json() as Promise<RunStatus>;
}

export async function listRuns(workflowName?: string): Promise<RunStatus[]> {
  const params = workflowName ? `?workflowName=${encodeURIComponent(workflowName)}` : '';
  const res = await fetch(`/api/runs${params}`);
  if (!res.ok) throw new Error(`GET runs failed: ${res.status}`);
  return res.json() as Promise<RunStatus[]>;
}

/** Check if server is available (suppresses console noise) */
export async function checkServerHealth(): Promise<boolean> {
  try {
    const res = await fetch('/api/health', { signal: AbortSignal.timeout(2000) });
    return res.ok;
  } catch {
    return false;
  }
}

// ---- Validation ----

export interface ValidationEntry {
  nodeId: string;
  message: string;
  severity: 'error' | 'warning' | 'info';
}

export async function validateWorkflow(workflowId: string): Promise<ValidationEntry[]> {
  const res = await fetch('/api/workflows/validate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ workflowId }),
  });
  if (!res.ok) throw new Error(`POST validate failed: ${res.status}`);
  return res.json() as Promise<ValidationEntry[]>;
}

export interface NodeValidationEntry {
  nodeId: string;
  field: string;
  message: string;
  severity: string;
}

export async function validateNodesBatch(nodes: Record<string, unknown>[]): Promise<NodeValidationEntry[]> {
  const res = await fetch('/api/nodes/validate-batch', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(nodes),
  });
  if (!res.ok) throw new Error(`POST validate-batch failed: ${res.status}`);
  return res.json() as Promise<NodeValidationEntry[]>;
}

// ---- Node Types Catalog ----

export interface ServerNodeType {
  runtimeType: string;
  label: string;
  category: string;
  description: string;
  ports: { in: boolean; out: boolean; error: boolean };
  outputPorts: string[];
  dynamicPorts: boolean;
  portLabels: Record<string, string> | null;
  events: string[];
  properties: ServerNodeProperty[];
}

export interface ServerNodeProperty {
  name: string;
  label: string;
  type: string;
  required: boolean;
  default: string | null;
  placeholder: string | null;
  options: string[] | null;
}

export async function fetchNodeTypes(): Promise<ServerNodeType[]> {
  const res = await fetch('/api/node-types');
  if (!res.ok) throw new Error(`GET /api/node-types failed: ${res.status}`);
  return res.json() as Promise<ServerNodeType[]>;
}

// ---- AI Registry ----

export interface AIRegistryItem {
  name: string;
  description: string;
  model: string | null;
}

export async function fetchAIAgents(): Promise<AIRegistryItem[]> {
  const res = await fetch('/api/ai/agents');
  if (!res.ok) throw new Error(`GET /api/ai/agents failed: ${res.status}`);
  return res.json() as Promise<AIRegistryItem[]>;
}

export async function fetchAISkills(): Promise<AIRegistryItem[]> {
  const res = await fetch('/api/ai/skills');
  if (!res.ok) throw new Error(`GET /api/ai/skills failed: ${res.status}`);
  return res.json() as Promise<AIRegistryItem[]>;
}

export interface AIInputParam {
  name: string;
  type: string;
  required: boolean;
  description: string;
}

export interface AIOutputField {
  name: string;
  type: string;
  description: string;
}

export interface AIMetadata {
  inputs: AIInputParam[];
  outputs: AIOutputField[];
  scenarios?: Record<string, { description?: string; inputs: AIInputParam[]; outputs: AIOutputField[] }>;
  defaultScenario?: string;
}

export async function fetchAgentInputs(name: string): Promise<AIMetadata> {
  const res = await fetch(`/api/ai/agents/${encodeURIComponent(name)}/inputs`);
  if (!res.ok) return { inputs: [], outputs: [] };
  return res.json() as Promise<AIMetadata>;
}

export async function fetchSkillInputs(name: string): Promise<AIMetadata> {
  const res = await fetch(`/api/ai/skills/${encodeURIComponent(name)}/inputs`);
  if (!res.ok) return { inputs: [], outputs: [] };
  return res.json() as Promise<AIMetadata>;
}
