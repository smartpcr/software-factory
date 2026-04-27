// force-reload: 1776811144
// -----------------------------------------------------------------------
// Entry point — boot and demo seed
// -----------------------------------------------------------------------

import './theme.css';
import { App } from './app';
import { Exec } from './exec';
import { State } from './state';
import { NodeTypes } from './registry';
import { uid, clone } from './utils';
import { Canvas } from './canvas';
import type { FlowNode } from './types';

// Expose for testing/debugging
(window as unknown as Record<string, unknown>).__App = App;
(window as unknown as Record<string, unknown>).__NodeTypes = NodeTypes;
(window as unknown as Record<string, unknown>).__State = State;

function seedDemo() {
  // Create a dedicated demo workflow — don't mutate whatever newWorkflow() made
  const wfId = uid('wf');
  const wf = {
    workflowId: wfId,
    workflowName: 'Demo: User Onboarding',
    nodes: [] as FlowNode[],
    edges: [] as { id: string; sourceNodeId: string; sourcePort: string; sourcePortName?: string; targetNodeId: string; targetPort: string }[],
    createdAt: Date.now(),
    updatedAt: Date.now(),
  };

  const addNode = (type: string, x: number, y: number, overrides: Partial<FlowNode> = {}): FlowNode => {
    const t = NodeTypes[type];
    const n: FlowNode = {
      nodeId: uid('n'), runtimeType: type, nodeName: t.label,
      x, y, width: 220,
      config: clone(t.config || {}),
      status: '', parentId: null, parentSlot: null,
      ...overrides
    };
    if (t.branches) n.branches = [...t.branches];
    wf.nodes.push(n);
    return n;
  };

  const start = addNode('start', 200, 100, { nodeName: 'Manual Trigger' });
  const http = addNode('http', 200, 280, {
    nodeName: 'Fetch User Data',
    config: { method: 'GET', url: 'https://api.example.com/user/42', headers: '{}' }
  });
  const ifNode = addNode('if', 200, 480, {
    nodeName: 'Is premium?',
    config: { condition: 'input.user.tier === "premium"' }
  });
  const ai = addNode('ai', -40, 720, {
    nodeName: 'Generate Welcome',
    config: {
      mode: 'skill', skillName: 'generate_welcome',
      model: 'claude-opus-4.6-1m',
      systemPrompt: 'You are a concise welcome message generator.',
      inputMapping: 'input.user', outputKey: 'message',
      confidence: 70, maxTokens: 500
    }
  });
  const log = addNode('log', 440, 720, {
    nodeName: 'Log free-tier',
    config: { level: 'info', message: 'Free-tier user visited' }
  });
  const forEach = addNode('foreach', -40, 920, {
    nodeName: 'For each message',
    config: { source: 'input.notifications', mode: 'foreach', maxIterations: 50 }
  });
  const send = addNode('action', -260, 1120, {
    nodeName: 'Send Notification',
    config: { actionType: 'function', handler: 'send_notif' }
  });
  const end = addNode('end', 200, 1320, { nodeName: 'Done' });

  const addEdge = (source: string, sourcePort: string, target: string, sourcePortName?: string) => {
    const id = uid('e');
    wf.edges.push({ id, sourceNodeId: source, sourcePort, sourcePortName, targetNodeId: target, targetPort: 'in' });
  };

  addEdge(start.nodeId, 'out', http.nodeId);
  addEdge(http.nodeId, 'out', ifNode.nodeId);
  addEdge(ifNode.nodeId, 'branch', ai.nodeId, 'true');
  addEdge(ifNode.nodeId, 'branch', log.nodeId, 'false');
  addEdge(ai.nodeId, 'out', forEach.nodeId);
  addEdge(forEach.nodeId, 'branch', send.nodeId, 'iterate');
  addEdge(forEach.nodeId, 'out', end.nodeId);
  addEdge(log.nodeId, 'out', end.nodeId);
  addEdge(send.nodeId, 'out', forEach.nodeId);

  // Register and load the demo workflow
  State.workflows[wfId] = wf;
  App.loadWorkflow(wfId);
  App.saveToStorage();
  App.log('info', null, 'Demo workflow loaded');
}

document.addEventListener('DOMContentLoaded', () => {
  App.init();
  Exec.setStatus('idle');
  // Demo seeding is deferred — App.init() loads server types asynchronously,
  // and seeds the demo after types are available (see app.ts fetchNodeTypes callback).
  if (Object.keys(State.workflows).length > 0 && !State.currentWfId) {
    App.newWorkflow(true);
  }
});
