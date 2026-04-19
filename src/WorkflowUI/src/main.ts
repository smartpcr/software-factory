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

function seedDemo() {
  State.nodes.clear();
  State.edges.clear();

  const addNode = (type: string, x: number, y: number, overrides: Partial<FlowNode> = {}): FlowNode => {
    const t = NodeTypes[type];
    const n: FlowNode = {
      id: uid('n'), type, name: t.label,
      x, y, width: 220,
      config: clone(t.config || {}),
      status: '', parentId: null, parentSlot: null,
      ...overrides
    };
    if (t.branches) n.branches = [...t.branches];
    State.nodes.set(n.id, n);
    return n;
  };

  const start = addNode('start', 200, 100, { name: 'Manual Trigger' });
  const http = addNode('http', 200, 280, {
    name: 'Fetch User Data',
    config: { method: 'GET', url: 'https://api.example.com/user/42', headers: '{}' }
  });
  const ifNode = addNode('if', 200, 480, {
    name: 'Is premium?',
    config: { condition: 'input.user.tier === "premium"' }
  });
  const ai = addNode('ai', -40, 720, {
    name: 'Generate Welcome',
    config: {
      mode: 'skill', skillId: 'generate_welcome',
      model: 'claude-opus-4-7',
      systemPrompt: 'You are a concise welcome message generator.',
      inputMapping: 'input.user', outputKey: 'message',
      temperature: 0.7, maxTokens: 500
    }
  });
  const log = addNode('log', 440, 720, {
    name: 'Log free-tier',
    config: { level: 'info', message: 'Free-tier user visited' }
  });
  const loop = addNode('loop', -40, 920, {
    name: 'For each message',
    config: { source: 'input.notifications', mode: 'foreach', maxIterations: 50 }
  });
  const send = addNode('action', -260, 1120, {
    name: 'Send Notification',
    config: { actionType: 'function', handler: 'send_notif' }
  });
  const end = addNode('end', 200, 1320, { name: 'Done' });

  const addEdge = (source: string, sourcePort: string, target: string, sourcePortName?: string) => {
    const id = uid('e');
    State.edges.set(id, { id, source, sourcePort, sourcePortName, target, targetPort: 'in' });
  };

  addEdge(start.id, 'out', http.id);
  addEdge(http.id, 'out', ifNode.id);
  addEdge(ifNode.id, 'branch', ai.id, 'true');
  addEdge(ifNode.id, 'branch', log.id, 'false');
  addEdge(ai.id, 'out', loop.id);
  addEdge(loop.id, 'branch', send.id, 'iterate');
  addEdge(loop.id, 'branch', end.id, 'complete');
  addEdge(log.id, 'out', end.id);
  addEdge(send.id, 'out', loop.id);

  State.isDirty = false;
  const wf = State.workflows[State.currentWfId || ''];
  if (wf) wf.name = 'Demo: User Onboarding';
  const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
  if (titleInput) titleInput.value = 'Demo: User Onboarding';
  App.renderAll();
  setTimeout(() => Canvas.fitToView(), 100);
  App.log('info', null, 'Demo workflow loaded');
}

document.addEventListener('DOMContentLoaded', () => {
  App.init();
  Exec.setStatus('idle');
  // Seed a demo workflow if nothing was loaded
  setTimeout(() => {
    if (State.nodes.size <= 1) seedDemo();
  }, 200);
});
