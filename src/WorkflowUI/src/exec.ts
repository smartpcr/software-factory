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
import { mountJsonEditor, disposeAllEditors as _disposeEditors } from './csharp-editor';

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
      // For Log nodes, display the resolved log message in the execution log
      const outData = event.payload?.outputData as Record<string, unknown> | undefined;
      if (node && node.runtimeType.toLowerCase() === 'log' && outData?.message) {
        const lvl = String(outData.level || 'info');
        const logLevel = lvl === 'error' ? 'error' : lvl === 'warn' || lvl === 'warning' ? 'warn' : 'info';
        logFn(logLevel, event.nodeId || null, `📝 [${lvl.toUpperCase()}] ${String(outData.message)}`);
      } else {
        logFn('success', event.nodeId || null, `✓ ${event.nodeName || event.nodeId} completed`);
      }
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
    case 'NodeLog': {
      const msg = (event.payload?.message as string) || '';
      if (msg) {
        const nodeName = event.nodeName || findNodeById(event.nodeId)?.nodeName || event.nodeId || '';
        logFn('info', event.nodeId || null, `🤖 [${nodeName}] ${msg}`);
      }
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
    State.nodes.forEach(n => { n.status = ''; n._runData = undefined; n._runDuration = undefined; n._runError = undefined; });
    State.edges.forEach(e => { e._active = false; e._runtimeData = undefined; });
    State.activeRun = null;
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

    // Check if workflow has a WebHook start trigger — show payload editor dialog
    let testPayload: string | undefined;
    const startNode = [...State.nodes.values()].find(
      n => n.runtimeType.toLowerCase() === 'start' && String(n.config?.triggerType) === 'WebHook',
    );
    if (startNode?.config?.bodyJsonSchema) {
      const sample = generateSampleFromSchema(String(startNode.config.bodyJsonSchema));
      const result = await showWebhookPayloadDialog(sample, String(startNode.config.webHookRoute || ''));
      if (result === null) {
        logFn('info', null, 'Run cancelled by user');
        this.setStatus('idle');
        return;
      }
      testPayload = result;
    }

    // Start real execution via API
    try {
      const result = await startRun(wf.workflowName, testPayload);
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
        const replayOut = event.payload?.outputData as Record<string, unknown> | undefined;
        const replayNode = event.nodeId ? findNodeById(event.nodeId) : null;
        if (replayNode && replayNode.runtimeType.toLowerCase() === 'log' && replayOut?.message) {
          const rlvl = String(replayOut.level || 'info');
          const rLogLevel = rlvl === 'error' ? 'error' : rlvl === 'warn' || rlvl === 'warning' ? 'warn' : 'info';
          log(rLogLevel, event.nodeId || null, `📝 [${rlvl.toUpperCase()}] ${String(replayOut.message)}`);
        } else {
          log('success', event.nodeId || null, `✓ ${event.nodeName || event.nodeId} completed ${dur ? `(${dur})` : ''}`);
        }
        break;
      }
      case 'NodeFailed':
        log('error', event.nodeId || null, `✗ ${event.nodeName || event.nodeId} failed: ${event.payload?.errorMessage || 'unknown'}`);
        break;
      case 'NodeLog': {
        const logMsg = (event.payload?.message as string) || '';
        if (logMsg) {
          const logNodeName = event.nodeName || findNodeById(event.nodeId)?.nodeName || event.nodeId || '';
          log('info', event.nodeId || null, `🤖 [${logNodeName}] ${logMsg}`);
        }
        break;
      }
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

/** Generate a sample JSON object from a JSON Schema string */
function generateSampleFromSchema(schemaStr: string): string {
  try {
    const schema = JSON.parse(schemaStr);
    const sample = generateSampleObject(schema);
    return JSON.stringify(sample, null, 2);
  } catch {
    return '{}';
  }
}

function generateSampleObject(schema: Record<string, unknown>): unknown {
  if (!schema || typeof schema !== 'object') return {};
  const type = schema.type as string;
  const format = schema.format as string | undefined;

  if (type === 'object') {
    const props = schema.properties as Record<string, Record<string, unknown>> | undefined;
    if (!props) return {};
    const obj: Record<string, unknown> = {};
    for (const [key, propSchema] of Object.entries(props)) {
      obj[key] = generateSampleObject(propSchema);
    }
    return obj;
  }
  if (type === 'array') {
    const items = schema.items as Record<string, unknown> | undefined;
    return items ? [generateSampleObject(items)] : [];
  }
  if (type === 'integer' || (type === 'number' && format?.startsWith('int'))) return 0;
  if (type === 'number') return 0.0;
  if (type === 'boolean') return false;
  if (type === 'string') {
    if (format === 'date-time') return new Date().toISOString();
    if (format === 'date') return new Date().toISOString().slice(0, 10);
    if (format === 'email') return 'user@example.com';
    if (format === 'uri') return 'https://example.com';
    return '';
  }
  return null;
}

/**
 * Show a modal dialog with a Monaco JSON editor for entering webhook test payload.
 * Returns the JSON string on confirm, or null on cancel.
 */
function showWebhookPayloadDialog(defaultValue: string, route: string): Promise<string | null> {
  return new Promise((resolve) => {
    let editorCleanup: (() => void) | null = null;
    let currentValue = defaultValue;

    // Backdrop
    const backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    backdrop.style.cssText = 'position:fixed;inset:0;z-index:9000;display:flex;align-items:center;justify-content:center;background:rgba(0,0,0,0.5);backdrop-filter:blur(4px);';

    // Dialog
    const dialog = document.createElement('div');
    dialog.className = 'modal';
    dialog.style.cssText = 'width:560px;max-height:80vh;display:flex;flex-direction:column;padding:0;overflow:hidden;';

    dialog.innerHTML = `
      <div style="padding:20px 24px 12px;">
        <h3 style="font-size:15px;margin:0 0 4px;">Test Webhook Payload</h3>
        <p style="color:var(--text-2);font-size:12px;margin:0;">
          POST <code style="background:var(--bg-2);padding:2px 6px;border-radius:3px;font-size:11px;">/webhook/${route}</code>
          — edit the JSON body below and click Run.
        </p>
      </div>
      <div style="flex:1;min-height:280px;padding:0 24px;">
        <div id="webhook-payload-editor" style="height:280px;border:1px solid var(--stroke-1);border-radius:6px;overflow:hidden;"></div>
      </div>
      <div style="padding:4px 24px 8px;">
        <div id="webhook-payload-error" style="font-size:11px;color:var(--err);min-height:16px;"></div>
      </div>
      <div class="modal-actions" style="padding:0 24px 20px;">
        <button class="modal-btn-secondary" id="webhook-cancel">Cancel</button>
        <button class="modal-btn-primary" id="webhook-run">▶ Run</button>
      </div>
    `;

    backdrop.appendChild(dialog);
    document.body.appendChild(backdrop);

    // Mount Monaco JSON editor
    requestAnimationFrame(() => {
      editorCleanup = mountJsonEditor('webhook-payload-editor', defaultValue, (val) => {
        currentValue = val;
        // Live JSON validation feedback
        const errEl = document.getElementById('webhook-payload-error');
        if (errEl) {
          try {
            JSON.parse(val);
            errEl.textContent = '';
          } catch (e) {
            errEl.textContent = `⚠ ${(e as Error).message}`;
          }
        }
      });
    });

    const cleanup = () => {
      if (editorCleanup) editorCleanup();
      backdrop.remove();
    };

    // Cancel
    dialog.querySelector('#webhook-cancel')!.addEventListener('click', () => {
      cleanup();
      resolve(null);
    });
    backdrop.addEventListener('click', (e) => {
      if (e.target === backdrop) { cleanup(); resolve(null); }
    });
    document.addEventListener('keydown', function onEsc(e) {
      if (e.key === 'Escape') { document.removeEventListener('keydown', onEsc); cleanup(); resolve(null); }
    });

    // Run
    dialog.querySelector('#webhook-run')!.addEventListener('click', () => {
      try {
        JSON.parse(currentValue);
      } catch (e) {
        const errEl = document.getElementById('webhook-payload-error');
        if (errEl) errEl.textContent = `⚠ ${(e as Error).message}`;
        return;
      }
      cleanup();
      resolve(currentValue);
    });
  });
}
