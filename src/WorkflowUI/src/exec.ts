// -----------------------------------------------------------------------
// Execution controller — real API execution with SignalR live updates,
// simulated fallback when server is unavailable
// -----------------------------------------------------------------------

import type { ExecState } from './types';
import { State } from './state';
import { Nodes } from './nodes';
import { Edges } from './edges';
import { startRun, cancelRun, saveWorkflow as saveWorkflowApi } from './api';
import type { WorkflowData } from './types';
import * as signalR from '@microsoft/signalr';

type LogFn = (level: string, nodeId: string | null, msg: string) => void;
type ClearLogsFn = () => void;

let logFn: LogFn;
let clearLogsFn: ClearLogsFn;
let hubConnection: signalR.HubConnection | null = null;
let currentRunId: string | null = null;

function getConnection(): signalR.HubConnection {
  if (!hubConnection) {
    hubConnection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/telemetry')
      .withAutomaticReconnect()
      .build();

    hubConnection.on('ReceiveRunEvent', (event: RunEvent) => {
      handleRunEvent(event);
    });
  }
  return hubConnection;
}

interface RunEvent {
  runId: string;
  seq: number;
  kind: string;
  synthetic: boolean;
  timestampUtc: string;
  nodeId?: string;
  nodeName?: string;
  payload?: Record<string, unknown>;
}

function handleRunEvent(event: RunEvent) {
  if (!event || event.runId !== currentRunId) return;

  switch (event.kind) {
    case 'NodeStarted': {
      const node = findNodeById(event.nodeId);
      if (node) { node.status = 'running'; Nodes.render(); }
      logFn('info', event.nodeId || null, `▶ ${event.nodeName || event.nodeId} started`);
      break;
    }
    case 'NodeCompleted': {
      const node = findNodeById(event.nodeId);
      if (node) { node.status = 'success'; Nodes.render(); }
      logFn('success', event.nodeId || null, `✓ ${event.nodeName || event.nodeId} completed`);
      // Animate edges
      if (event.nodeId) {
        State.edges.forEach(e => {
          if (e.sourceNodeId === event.nodeId) { e._active = true; }
        });
        Edges.render();
        setTimeout(() => {
          State.edges.forEach(e => { if (e.sourceNodeId === event.nodeId) e._active = false; });
          Edges.render();
        }, 500);
      }
      break;
    }
    case 'NodeFailed': {
      const node = findNodeById(event.nodeId);
      if (node) { node.status = 'failed'; Nodes.render(); }
      const errMsg = (event.payload?.errorMessage as string) || 'Unknown error';
      logFn('error', event.nodeId || null, `✗ ${event.nodeName || event.nodeId} failed: ${errMsg}`);
      break;
    }
    case 'NodeCancelled': {
      const node = findNodeById(event.nodeId);
      if (node) { node.status = 'cancelled'; Nodes.render(); }
      logFn('warn', event.nodeId || null, `■ ${event.nodeName || event.nodeId} cancelled`);
      break;
    }
    case 'EdgeActivated': {
      // Synthetic event from EdgeActivationDeriver
      break;
    }
    case 'WorkflowCompleted': {
      Exec.setStatus('success');
      logFn('success', null, '✓ Workflow completed successfully');
      detachRun();
      break;
    }
    case 'WorkflowFailed': {
      Exec.setStatus('failed');
      const errMsg = (event.payload?.errorMessage as string) || 'Unknown error';
      logFn('error', null, `✗ Workflow failed: ${errMsg}`);
      detachRun();
      break;
    }
    case 'WorkflowCancelled': {
      Exec.setStatus('cancelled');
      logFn('warn', null, '■ Workflow cancelled');
      detachRun();
      break;
    }
  }
}

function findNodeById(nodeId?: string) {
  if (!nodeId) return null;
  // Try direct match first, then by nodeId field
  for (const n of State.nodes.values()) {
    if (n.nodeId === nodeId) return n;
  }
  return null;
}

async function attachRun(runId: string) {
  currentRunId = runId;
  try {
    const conn = getConnection();
    if (conn.state === signalR.HubConnectionState.Disconnected) {
      await conn.start();
    }
    await conn.invoke('AttachRun', runId);
    // Replay buffered events that happened before we attached
    const replay = await conn.invoke('Replay', runId, 0) as { events?: RunEvent[]; notFound?: boolean };
    if (replay?.events) {
      for (const event of replay.events) {
        handleRunEvent(event);
      }
    }
  } catch (err) {
    logFn('warn', null, 'SignalR connection failed — using polling fallback');
  }
}

function detachRun() {
  if (currentRunId && hubConnection) {
    try {
      hubConnection.invoke('DetachRun', currentRunId);
    } catch { /* ignore */ }
  }
  currentRunId = null;
}

export const Exec = {
  state: 'idle' as ExecState,

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
    if (btnPause) btnPause.disabled = true; // Pause not supported with real engine
    if (btnResume) btnResume.disabled = !(status === 'failed' || status === 'cancelled');
    if (btnRerun) btnRerun.disabled = !(status === 'success' || status === 'failed' || status === 'cancelled');
    if (btnCancel) btnCancel.disabled = !(status === 'running');
  },

  reset() {
    State.nodes.forEach(n => { n.status = ''; });
    State.edges.forEach(e => { e._active = false; });
    Nodes.render();
    Edges.render();
  },

  async run() {
    this.reset();
    clearLogsFn();
    logFn('info', null, '▶ Workflow execution started');

    if (!State.currentWfId) {
      logFn('error', null, 'No workflow loaded');
      this.setStatus('failed');
      return;
    }

    const wf = State.workflows[State.currentWfId];
    if (!wf) {
      logFn('error', null, 'Workflow not found');
      this.setStatus('failed');
      return;
    }

    // Save workflow to server first so the executor has the latest version
    try {
      await saveWorkflowApi(wf.workflowName, wf);
    } catch {
      logFn('warn', null, 'Could not save to server before execution');
    }

    // Start real execution via API
    try {
      const result = await startRun(wf.workflowName);
      this.setStatus('running');
      logFn('info', null, `Run started (ID: ${result.runId})`);

      // Try SignalR for live updates (non-blocking)
      attachRun(result.runId).catch(() => {});

      // Poll for completion as fallback (SignalR may miss fast executions)
      this.pollRunStatus(result.runId);
    } catch (err) {
      logFn('error', null, `Failed to start: ${(err as Error).message}`);
      this.setStatus('failed');
    }
  },

  /** Poll run status until terminal state */
  async pollRunStatus(runId: string) {
    const poll = async () => {
      try {
        const res = await fetch(`/api/runs/${runId}`);
        if (!res.ok) return;
        const data = await res.json();
        const status = data.status?.toLowerCase();

        if (status === 'completed') {
          if (this.state === 'running') {
            // Mark all nodes as success if we missed individual events
            State.nodes.forEach(n => {
              if (n.status === 'running' || n.status === '') n.status = 'success';
            });
            Nodes.render();
            this.setStatus('success');
            logFn('success', null, '✓ Workflow completed');
            detachRun();
          }
        } else if (status === 'failed') {
          if (this.state === 'running') {
            State.nodes.forEach(n => {
              if (n.status === 'running') n.status = 'failed';
            });
            Nodes.render();
            this.setStatus('failed');
            const errMsg = data.error || 'Unknown error';
            logFn('error', null, `✗ Workflow failed: ${errMsg}`);
            detachRun();
          }
        } else if (status === 'cancelled') {
          if (this.state === 'running') {
            this.setStatus('cancelled');
            logFn('warn', null, '■ Workflow cancelled');
            detachRun();
          }
        } else {
          // Still running — poll again
          setTimeout(poll, 500);
        }
      } catch {
        // Server unavailable, try again
        setTimeout(poll, 2000);
      }
    };
    // Start polling after a short delay to give SignalR a chance
    setTimeout(poll, 300);
  },

  async cancel() {
    if (!currentRunId) {
      logFn('warn', null, 'No active run to cancel');
      return;
    }
    try {
      await cancelRun(currentRunId);
      logFn('warn', null, '■ Cancel requested');
    } catch (err) {
      logFn('error', null, `Cancel failed: ${(err as Error).message}`);
    }
  },

  pause() {
    logFn('warn', null, 'Pause is not supported with real engine execution');
  },

  resume() {
    if (this.state === 'failed' || this.state === 'cancelled') {
      logFn('info', null, '↻ Rerunning workflow');
      this.run();
    }
  },

  rerun() {
    logFn('info', null, '↻ Rerunning workflow');
    this.run();
  },
};

/** Replay a historical run's events from the server buffer */
export async function replayRunEvents(
  runId: string,
  log: (level: string, nodeId: string | null, msg: string) => void
): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    await conn.start();
  }
  const replay = await conn.invoke('Replay', runId, 0) as {
    events?: RunEvent[];
    notFound?: boolean;
    truncated?: boolean;
  };
  if (replay?.notFound) {
    throw new Error('Run not found in replay buffer (may have been evicted)');
  }
  if (!replay?.events?.length) {
    log('warn', null, 'No events available for this run');
    return;
  }
  // Replay events with a small delay between each for visual effect
  for (let i = 0; i < replay.events.length; i++) {
    const event = replay.events[i];
    handleRunEvent(event);
    // Log each event
    switch (event.kind) {
      case 'WorkflowStarted':
        log('info', null, `▶ Workflow started (${(event.payload?.totalNodes as number) || '?'} nodes)`);
        break;
      case 'NodeStarted':
        log('info', event.nodeId || null, `▶ ${event.nodeName || event.nodeId} started`);
        break;
      case 'NodeCompleted': {
        const dur = event.payload?.duration as string || '';
        log('success', event.nodeId || null, `✓ ${event.nodeName || event.nodeId} completed ${dur ? `(${dur})` : ''}`);
        break;
      }
      case 'NodeFailed':
        log('error', event.nodeId || null, `✗ ${event.nodeName || event.nodeId} failed: ${event.payload?.errorMessage || 'unknown'}`);
        break;
      case 'WorkflowCompleted':
        log('success', null, `✓ Workflow completed (${event.payload?.duration || ''})`);
        break;
      case 'WorkflowFailed':
        log('error', null, `✗ Workflow failed`);
        break;
    }
    // Small delay for visual replay effect (skip for last event)
    if (i < replay.events.length - 1) {
      await new Promise(r => setTimeout(r, 120));
    }
  }
}
