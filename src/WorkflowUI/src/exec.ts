// -----------------------------------------------------------------------
// Execution engine — simulated + API-backed
// -----------------------------------------------------------------------

import type { FlowNode, ExecState } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { Nodes } from './nodes';
import { Edges } from './edges';
import { runWorkflowApi, cancelWorkflowApi } from './api';

type LogFn = (level: string, nodeId: string | null, msg: string) => void;
type ClearLogsFn = () => void;

let logFn: LogFn;
let clearLogsFn: ClearLogsFn;

export const Exec = {
  state: 'idle' as ExecState,
  queue: [] as string[],
  executing: new Set<string>(),
  completed: new Set<string>(),
  failed: new Set<string>(),
  lastFailed: null as string[] | null,
  timers: [] as ReturnType<typeof setTimeout>[],
  paused: false,

  init(log: LogFn, clearLogs: ClearLogsFn) {
    logFn = log;
    clearLogsFn = clearLogs;
  },

  setStatus(status: ExecState) {
    this.state = status;
    const ind = document.getElementById('execIndicator');
    if (ind) ind.className = 'exec-indicator ' + status;
    const statusEl = document.getElementById('execStatus');
    if (statusEl) statusEl.textContent = status.toUpperCase();
    const btnRun = document.getElementById('btnRun') as HTMLButtonElement | null;
    const btnPause = document.getElementById('btnPause') as HTMLButtonElement | null;
    const btnResume = document.getElementById('btnResume') as HTMLButtonElement | null;
    const btnRerun = document.getElementById('btnRerun') as HTMLButtonElement | null;
    const btnCancel = document.getElementById('btnCancel') as HTMLButtonElement | null;
    if (btnRun) btnRun.disabled = status === 'running' || status === 'paused';
    if (btnPause) btnPause.disabled = status !== 'running';
    if (btnResume) btnResume.disabled = !(status === 'paused' || status === 'failed' || status === 'cancelled');
    if (btnRerun) btnRerun.disabled = !(status === 'success' || status === 'failed' || status === 'cancelled');
    if (btnCancel) btnCancel.disabled = !(status === 'running' || status === 'paused');
  },

  reset() {
    this.queue = [];
    this.executing.clear();
    this.completed.clear();
    this.failed.clear();
    this.lastFailed = null;
    this.paused = false;
    this.clearTimers();
    State.nodes.forEach(n => { n.status = ''; });
    State.edges.forEach(e => { e._active = false; });
    Nodes.render();
    Edges.render();
  },

  clearTimers() {
    this.timers.forEach(t => clearTimeout(t));
    this.timers = [];
  },

  async run() {
    this.reset();
    clearLogsFn();
    logFn('info', null, '▶ Workflow execution started');

    // Always use simulated execution (server execution via future SignalR integration)

    // Fallback: simulated execution
    const starts = [...State.nodes.values()].filter(n => n.runtimeType === 'start' || n.runtimeType === 'trigger');
    if (!starts.length) {
      logFn('error', null, 'No start node found. Add a Start or Trigger node.');
      this.setStatus('failed');
      return;
    }
    this.setStatus('running');
    starts.forEach(n => this.executeNode(n.nodeId));
  },

  cancel() {
    logFn('warn', null, '■ Workflow cancelled');

    this.clearTimers();
    this.executing.forEach(id => {
      const node = State.nodes.get(id);
      if (node) node.status = 'cancelled';
    });
    this.executing.clear();
    State.nodes.forEach(n => {
      if (n.status === 'running') n.status = 'cancelled';
    });
    Nodes.render();
    this.setStatus('cancelled');
    this.lastFailed = [...State.nodes.values()].filter(n => n.status === 'cancelled').map(n => n.nodeId);
  },

  pause() {
    if (this.state !== 'running') return;
    this.paused = true;
    logFn('warn', null, '⏸ Workflow paused');
    this.setStatus('paused');
    this.executing.forEach(id => {
      const node = State.nodes.get(id);
      if (node && node.status === 'running') node.status = 'paused';
    });
    Nodes.render();
  },

  resume() {
    if (this.state === 'paused') {
      this.paused = false;
      logFn('info', null, '▶ Workflow resumed');
      this.setStatus('running');
      const toResume = [...this.executing];
      this.executing.clear();
      toResume.forEach(id => {
        const node = State.nodes.get(id);
        if (node) node.status = '';
        this.executeNode(id);
      });
    } else if (this.state === 'failed' || this.state === 'cancelled') {
      logFn('info', null, '▶ Resuming from failure point');
      const failedIds = this.lastFailed || [...this.failed];
      if (!failedIds.length) return;
      failedIds.forEach(id => {
        const node = State.nodes.get(id);
        if (node) node.status = '';
        this.failed.delete(id);
      });
      this.paused = false;
      this.setStatus('running');
      failedIds.forEach(id => this.executeNode(id));
    }
  },

  rerun() {
    logFn('info', null, '↻ Rerunning workflow from start');
    this.run();
  },

  executeNode(nodeId: string) {
    const node = State.nodes.get(nodeId);
    if (!node) return;
    if (this.paused) { this.executing.add(nodeId); return; }
    if (this.completed.has(nodeId) && node.runtimeType !== 'foreach') return;
    if (this.executing.has(nodeId)) return;

    this.executing.add(nodeId);
    node.status = 'running';
    Nodes.render();
    logFn('info', nodeId, `Executing ${NodeTypes[node.runtimeType]?.label || node.runtimeType}`);

    const duration = this.simulatedDuration(node);
    const timer = setTimeout(() => {
      if (this.paused) return;
      if (this.state === 'cancelled') return;

      const shouldFail = node._forceFail || (Math.random() < 0.02 && node.runtimeType !== 'start' && node.runtimeType !== 'end');
      if (shouldFail) {
        this.onNodeFail(node);
      } else {
        this.onNodeSuccess(node);
      }
    }, duration);
    this.timers.push(timer);
  },

  simulatedDuration(node: FlowNode): number {
    if (node.runtimeType === 'start' || node.runtimeType === 'trigger') return 200;
    if (node.runtimeType === 'delay') {
      const c = node.config || {};
      let ms = parseInt(String(c.duration)) || 1000;
      if (c.unit === 's') ms *= 1000;
      if (c.unit === 'm') ms *= 60000;
      return Math.min(ms, 2000);
    }
    if (node.runtimeType === 'ai') return 800 + Math.random() * 1200;
    if (node.runtimeType === 'http') return 500 + Math.random() * 700;
    if (node.runtimeType === 'foreach') return 500;
    return 400 + Math.random() * 400;
  },

  onNodeSuccess(node: FlowNode) {
    node.status = 'success';
    this.executing.delete(node.nodeId);
    this.completed.add(node.nodeId);
    logFn('success', node.nodeId, '✓ Completed');
    Nodes.render();

    const type = NodeTypes[node.runtimeType];
    let edges: typeof State.edges extends Map<string, infer V> ? V[] : never[] = [];

    if (type?.branches && node.branches) {
      if (node.runtimeType === 'if') {
        const branch = Math.random() > 0.5 ? 'true' : 'false';
        edges = Edges.getSuccessors(node.nodeId, 'branch', branch);
        logFn('info', node.nodeId, `Condition evaluated: ${branch}`);
      } else if (node.runtimeType === 'switch') {
        const branch = node.branches[Math.floor(Math.random() * node.branches.length)];
        edges = Edges.getSuccessors(node.nodeId, 'branch', branch);
        logFn('info', node.nodeId, `Matched case: ${branch}`);
      } else if (node.runtimeType === 'parallel') {
        node.branches.forEach(b => {
          edges.push(...Edges.getSuccessors(node.nodeId, 'branch', b));
        });
        logFn('info', node.nodeId, `Fan-out: ${node.branches.length} branches`);
      } else if (node.runtimeType === 'foreach') {
        const iterCount = (node._iterCount || 0) + 1;
        node._iterCount = iterCount;
        if (iterCount <= 3) {
          edges = Edges.getSuccessors(node.nodeId, 'branch', 'iterate');
          logFn('info', node.nodeId, `Iteration ${iterCount}`);
          edges.forEach(e => {
            this.completed.delete(e.targetNodeId);
            const targetNode = State.nodes.get(e.targetNodeId);
            if (targetNode) targetNode.status = '';
          });
          Nodes.render();
        } else {
          edges = Edges.getSuccessors(node.nodeId, 'out');
          node._iterCount = 0;
          logFn('info', node.nodeId, 'ForEach complete');
        }
      }
    } else {
      edges = Edges.getSuccessors(node.nodeId, 'out');
    }

    edges.forEach(e => { e._active = true; });
    Edges.render();

    setTimeout(() => {
      edges.forEach(e => { e._active = false; });
      Edges.render();
      edges.forEach(e => this.executeNode(e.targetNodeId));
      this.checkCompletion();
    }, 400);
  },

  onNodeFail(node: FlowNode) {
    node.status = 'failed';
    this.executing.delete(node.nodeId);
    this.failed.add(node.nodeId);
    logFn('error', node.nodeId, '✗ Failed — ' + this.randomError());
    Nodes.render();

    const errEdges = Edges.getSuccessors(node.nodeId, 'error');
    if (errEdges.length) {
      logFn('info', node.nodeId, `Routing to error handler (${errEdges.length} edge${errEdges.length > 1 ? 's' : ''})`);
      errEdges.forEach(e => { e._active = true; });
      Edges.render();
      setTimeout(() => {
        errEdges.forEach(e => { e._active = false; });
        Edges.render();
        errEdges.forEach(e => this.executeNode(e.targetNodeId));
        this.checkCompletion();
      }, 400);
    } else {
      this.lastFailed = [node.nodeId];
      this.setStatus('failed');
      logFn('error', null, '✗ Workflow failed');
    }
  },

  randomError(): string {
    const errors = ['Connection timeout', 'Validation failed', 'Upstream 500', 'Rate limited', 'Malformed response'];
    return errors[Math.floor(Math.random() * errors.length)];
  },

  checkCompletion() {
    if (this.state !== 'running') return;
    if (this.executing.size === 0) {
      if (this.failed.size > 0) {
        this.lastFailed = [...this.failed];
        this.setStatus('failed');
        logFn('error', null, `✗ Workflow failed (${this.failed.size} failed node${this.failed.size > 1 ? 's' : ''})`);
      } else {
        this.setStatus('success');
        logFn('success', null, '✓ Workflow completed successfully');
      }
    }
  }
};
