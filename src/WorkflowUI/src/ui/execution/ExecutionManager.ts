import { useGraphStore } from '../state/graphStore';
import { useLogStore } from '../logs/logStore';
import type { NodeExecutionStatus } from '../../engine-contract';

export type ExecutionState = 'idle' | 'running' | 'paused' | 'success' | 'failed' | 'cancelled';

interface ExecutionContext {
  state: ExecutionState;
  queue: string[];
  executing: Set<string>;
  completed: Set<string>;
  failed: Set<string>;
  timers: ReturnType<typeof setTimeout>[];
}

const ctx: ExecutionContext = {
  state: 'idle',
  queue: [],
  executing: new Set(),
  completed: new Set(),
  failed: new Set(),
  timers: [],
};

let listeners: Array<() => void> = [];

function notify() {
  listeners.forEach((l) => l());
}

function setStatus(nodeId: string, status: NodeExecutionStatus) {
  useGraphStore.getState().setExecutionStatus(nodeId, status);
}

function log(level: 'info' | 'success' | 'warn' | 'error', nodeId: string | null, msg: string) {
  useLogStore.getState().addEntry({ level, nodeId, msg });
}

function clearTimers() {
  ctx.timers.forEach((t) => clearTimeout(t));
  ctx.timers = [];
}

function reset() {
  ctx.queue = [];
  ctx.executing.clear();
  ctx.completed.clear();
  ctx.failed.clear();
  clearTimers();
  useGraphStore.getState().clearExecutionStatuses();
}

function simulatedDuration(runtimeType: string): number {
  if (runtimeType === 'Timer') return 200;
  if (runtimeType === 'Subflow') return 800 + Math.random() * 400;
  return 400 + Math.random() * 400;
}

function executeNode(nodeId: string) {
  const store = useGraphStore.getState();
  const node = store.nodes.find((n) => n.nodeId === nodeId);
  if (!node) return;
  if (ctx.state === 'cancelled') return;
  if (ctx.state === 'paused') { ctx.executing.add(nodeId); return; }
  if (ctx.completed.has(nodeId)) return;
  if (ctx.executing.has(nodeId)) return;

  ctx.executing.add(nodeId);
  setStatus(nodeId, 'Running');
  log('info', nodeId, `Executing ${node.runtimeType}`);

  const duration = simulatedDuration(node.runtimeType);
  const timer = setTimeout(() => {
    if (ctx.state === 'cancelled' || ctx.state === 'paused') return;

    const shouldFail = Math.random() < 0.02 && node.runtimeType !== 'Container';

    if (shouldFail) {
      setStatus(nodeId, 'Failed');
      ctx.executing.delete(nodeId);
      ctx.failed.add(nodeId);
      log('error', nodeId, '✗ Failed');

      // Fire error edges
      const errorEdges = store.connections.filter(
        (c) => c.sourceNodeId === nodeId && c.triggerMessageType === 'Fail',
      );
      errorEdges.forEach((e) => executeNode(e.targetNodeId));

      if (ctx.executing.size === 0) {
        ctx.state = 'failed';
        log('error', null, '■ Workflow failed');
        notify();
      }
    } else {
      setStatus(nodeId, 'Completed');
      ctx.executing.delete(nodeId);
      ctx.completed.add(nodeId);
      log('success', nodeId, '✓ Completed');

      // Follow success edges
      const nextEdges = store.connections.filter(
        (c) => c.sourceNodeId === nodeId && c.triggerMessageType === 'Complete' && c.isEnabled,
      );
      nextEdges.forEach((e) => executeNode(e.targetNodeId));

      // Check if done
      if (ctx.executing.size === 0 && nextEdges.length === 0) {
        ctx.state = 'success';
        log('success', null, '✓ Workflow completed');
        notify();
      }
    }
  }, duration);
  ctx.timers.push(timer);
}

export const ExecutionManager = {
  getState(): ExecutionState {
    return ctx.state;
  },

  subscribe(listener: () => void): () => void {
    listeners.push(listener);
    return () => {
      listeners = listeners.filter((l) => l !== listener);
    };
  },

  run() {
    reset();
    useLogStore.getState().clear();
    log('info', null, '▶ Workflow execution started');
    ctx.state = 'running';
    notify();

    const store = useGraphStore.getState();
    // Find start nodes (nodes with no incoming connections)
    const targetIds = new Set(store.connections.map((c) => c.targetNodeId));
    const startNodes = store.nodes.filter((n) => !targetIds.has(n.nodeId));

    if (startNodes.length === 0 && store.nodes.length > 0) {
      // Fallback: execute first node
      const first = store.nodes[0];
      if (first) executeNode(first.nodeId);
    } else {
      startNodes.forEach((n) => executeNode(n.nodeId));
    }
  },

  pause() {
    if (ctx.state !== 'running') return;
    ctx.state = 'paused';
    log('warn', null, '⏸ Workflow paused');
    ctx.executing.forEach((id) => setStatus(id, 'Pending'));
    notify();
  },

  resume() {
    if (ctx.state === 'paused') {
      ctx.state = 'running';
      log('info', null, '▶ Workflow resumed');
      const toResume = [...ctx.executing];
      ctx.executing.clear();
      toResume.forEach((id) => executeNode(id));
      notify();
    } else if (ctx.state === 'failed' || ctx.state === 'cancelled') {
      ctx.state = 'running';
      log('info', null, '▶ Resuming from failure point');
      const failedIds = [...ctx.failed];
      ctx.failed.clear();
      failedIds.forEach((id) => {
        setStatus(id, 'Pending');
        executeNode(id);
      });
      notify();
    }
  },

  rerun() {
    log('info', null, '↻ Rerunning workflow from start');
    this.run();
  },

  cancel() {
    if (ctx.state !== 'running' && ctx.state !== 'paused') return;
    clearTimers();
    ctx.executing.forEach((id) => setStatus(id, 'Cancelled'));
    ctx.executing.clear();
    ctx.state = 'cancelled';
    log('warn', null, '■ Workflow cancelled');
    notify();
  },
};
