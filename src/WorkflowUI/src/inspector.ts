// -----------------------------------------------------------------------
// Inspector panel rendering
// -----------------------------------------------------------------------

import type { FlowNode, AppCallbacks, NodeTypeDefinition, WorkflowData } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { escapeHtml } from './utils';
import { Nodes } from './nodes';
import { aiModels, tierColor } from './config';
import { mountCSharpEditor, mountJsonEditor, disposeAllEditors } from './csharp-editor';
import { mountTemplateEditor } from './template-editor';
import { discoverInboundFields } from './inboundSchema';
import type { InboundField } from './inboundSchema';
import { fetchAIAgents, fetchAISkills, fetchAgentInputs, fetchSkillInputs, listWorkflows } from './api';
import type { AIRegistryItem, AIInputParam, AIOutputField } from './api';
import { renderNodeConfig } from './node-inspectors';
import type { InspectorContext } from './node-inspectors';

let callbacks: AppCallbacks;

/** Cached server workflow summaries for subflow dropdown */
let cachedServerWorkflows: { id: string; workflowId?: string; workflowName?: string }[] = [];
let serverWorkflowsLoaded = false;
export async function ensureServerWorkflows(): Promise<typeof cachedServerWorkflows> {
  if (!serverWorkflowsLoaded) {
    try {
      const list = await listWorkflows() as { id: string; workflowId?: string; workflowName?: string }[];
      cachedServerWorkflows = list;
      serverWorkflowsLoaded = true;
    } catch { /* ignore */ }
  }
  return cachedServerWorkflows;
}
export function invalidateServerWorkflows() { serverWorkflowsLoaded = false; }

// Cached AI registry items
let cachedAgents: AIRegistryItem[] = [];
let cachedSkills: AIRegistryItem[] = [];
let registryLoaded = false;

async function ensureAIRegistry(): Promise<void> {
  if (registryLoaded) return;
  try {
    [cachedAgents, cachedSkills] = await Promise.all([fetchAIAgents(), fetchAISkills()]);
    registryLoaded = true;
  } catch (e) {
    console.warn('Failed to load AI registry:', e);
  }
}

// Cached input/output params for the currently selected skill/agent
let cachedInputParams: AIInputParam[] = [];
let cachedOutputFields: AIOutputField[] = [];
let cachedInputParamsKey = '';

async function loadInputParams(mode: string, name: string): Promise<AIInputParam[]> {
  const key = `${mode}:${name}`;
  if (key === cachedInputParamsKey) return cachedInputParams;
  try {
    const metadata = mode === 'skill'
      ? await fetchSkillInputs(name)
      : await fetchAgentInputs(name);
    cachedInputParams = metadata.inputs;
    cachedOutputFields = metadata.outputs;
    cachedInputParamsKey = key;
    // Store outputs on window for inboundSchema to read (avoids circular dependency)
    const outputsMap = ((window as Record<string, unknown>).__cachedAIOutputs || {}) as Record<string, unknown>;
    outputsMap[key] = metadata.outputs;
    (window as Record<string, unknown>).__cachedAIOutputs = outputsMap;
  } catch {
    cachedInputParams = [];
  }
  return cachedInputParams;
}
let nodeRenderTimer: ReturnType<typeof setTimeout> | null = null;
function debouncedNodeRender() {
  if (nodeRenderTimer) clearTimeout(nodeRenderTimer);
  nodeRenderTimer = setTimeout(() => Nodes.render(), 300);
}

const chevronSvg = '<svg class="ins-chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 9l6 6 6-6"/></svg>';

/** Wrap content in a collapsible section */
function insSection(title: string, content: string, collapsed = false): string {
  return `<div class="ins-section ${collapsed ? 'collapsed' : ''}">
    <div class="ins-section-title">${chevronSvg}<span>${title}</span></div>
    <div class="ins-section-body">${content}</div>
  </div>`;
}

/** Bind collapse toggles on all section titles within a container */
function bindSectionToggles(container: HTMLElement) {
  container.querySelectorAll('.ins-section-title').forEach(title => {
    title.addEventListener('click', (e) => {
      // Don't toggle if clicking an input/select inside the title
      if ((e.target as HTMLElement).tagName === 'INPUT' || (e.target as HTMLElement).tagName === 'SELECT') return;
      const section = title.closest('.ins-section');
      if (section) section.classList.toggle('collapsed');
    });
  });
}

export const Inspector = {
  init(cb: AppCallbacks) {
    callbacks = cb;
    // Pre-load AI registry so dropdowns are ready
    ensureAIRegistry().then(() => { if (State.selection.kind === 'node') this.render(); });
  },

  render() {
    const el = document.getElementById('inspector');
    if (!el) return;
    // Preserve the panel-toggle button if present
    const toggle = el.querySelector('.panel-toggle-right');

    const { kind, id } = State.selection;
    if (!kind || !id) {
      this.renderWorkflowProperties(el);
      return;
    }
    if (kind === 'edge') { this.renderEdge(id); return; }
    if (kind === 'node') { this.renderNode(id); return; }
  },

  renderWorkflowProperties(el: HTMLElement) {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) {
      el.innerHTML = `<div class="ins-empty">
        <svg class="ins-empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="9"/><path d="M12 8v4l2 2"/></svg>
        <div>No workflow loaded</div>
      </div>`;
      return;
    }

    const fmtDate = (ts: number) => ts ? new Date(ts).toLocaleString() : '—';
    const nodeCount = State.nodes.size;
    const edgeCount = State.edges.size;

    el.innerHTML = `
      <div class="ins-header">
        <div class="ins-title">
          <div class="node-icon" style="color: var(--accent); width: 24px; height: 24px;">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M13 2v7h7"/></svg>
          </div>
          <div>
            <div class="ins-title-text">Workflow</div>
            <div class="ins-subtitle mono">${nodeCount} nodes · ${edgeCount} edges</div>
          </div>
        </div>
      </div>
      ${this.workflowRunHtml()}
      ${insSection('Properties', `
        <div class="ins-field">
          <label class="ins-field-label">Name</label>
          <input value="${escapeHtml(wf.workflowName || '')}" data-bind="wf-workflowName" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Description</label>
          <textarea placeholder="Describe what this workflow does…" data-bind="wf-description">${escapeHtml(wf.description || '')}</textarea>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Version</label>
          <input value="${escapeHtml(wf.version || '1.0')}" placeholder="1.0" data-bind="wf-version" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Author</label>
          <input value="${escapeHtml(wf.author || '')}" placeholder="e.g. your name" data-bind="wf-author" />
        </div>
      `)}
      ${insSection('Execution', `
        <div class="ins-field">
          <label class="ins-field-label">Entry point node</label>
          <select data-bind="wf-entryPointNodeId">
            <option value="">— auto-detect —</option>
            ${[...State.nodes.values()].map(n =>
              `<option value="${n.nodeId}" ${wf.entryPointNodeId === n.nodeId ? 'selected' : ''}>${escapeHtml(n.nodeName || n.nodeId)}</option>`
            ).join('')}
          </select>
          <div class="ins-field-hint">If not set, nodes without incoming connections are used as entry points.</div>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Max concurrency</label>
          <input type="number" min="0" value="${wf.maxConcurrency || 0}" data-bind="wf-maxConcurrency" />
          <div class="ins-field-hint">Maximum concurrent node executions. 0 = unlimited.</div>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Timeout (seconds)</label>
          <input type="number" min="0" value="${wf.timeoutSeconds || 0}" data-bind="wf-timeoutSeconds" />
          <div class="ins-field-hint">Entire workflow timeout. 0 = no timeout.</div>
        </div>
        <div class="ins-field">
          <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
            <input type="checkbox" ${wf.allowPause !== false ? 'checked' : ''} data-bind="wf-allowPause" style="width:auto" />
            Allow pause
          </label>
          <div class="ins-field-hint">Whether the workflow execution can be paused.</div>
        </div>
      `, true)}
      ${insSection('Variables', `
        <div class="var-editor" id="varEditor">
          <div class="var-editor-header">
            <span class="var-editor-col var-col-name">Name</span>
            <span class="var-editor-col var-col-type">Type</span>
            <span class="var-editor-col var-col-value">Default</span>
            <span class="var-editor-col var-col-actions"></span>
          </div>
          <div class="var-editor-rows" id="varEditorRows">
            ${this.renderVariableRows(wf.defaultVariables)}
          </div>
          <button class="var-editor-add" id="varEditorAdd">
            <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>
            Add variable
          </button>
        </div>
        <div class="ins-field-hint" style="margin-top:4px;">Global variables available to all nodes at runtime.</div>
      `, true)}
      ${insSection('Info', `
        <div class="ins-field">
          <label class="ins-field-label">Created</label>
          <input readonly value="${fmtDate(wf.createdAt)}" class="mono" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Last modified</label>
          <input readonly value="${fmtDate(wf.updatedAt)}" class="mono" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Workflow ID</label>
          <input readonly value="${escapeHtml(wf.workflowId)}" class="mono" />
        </div>
      `, true)}
    `;

    // Bind editable fields
    el.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>('[data-bind]').forEach(input => {
      const event = input.tagName === 'SELECT' ? 'change' : 'input';
      input.addEventListener(event, () => {
        const field = input.dataset.bind;
        if (!wf) return;
        switch (field) {
          case 'wf-workflowName':
            wf.workflowName = input.value;
            const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
            if (titleInput) titleInput.value = input.value;
            callbacks.markDirty();
            break;
          case 'wf-description': wf.description = input.value; callbacks.markDirty(); break;
          case 'wf-version': wf.version = input.value; callbacks.markDirty(); break;
          case 'wf-author': wf.author = input.value; callbacks.markDirty(); break;
          case 'wf-entryPointNodeId': wf.entryPointNodeId = input.value || undefined; callbacks.markDirty(); break;
          case 'wf-maxConcurrency': wf.maxConcurrency = parseInt(input.value) || 0; callbacks.markDirty(); break;
          case 'wf-timeoutSeconds': wf.timeoutSeconds = parseInt(input.value) || 0; callbacks.markDirty(); break;
          case 'wf-allowPause': wf.allowPause = (input as HTMLInputElement).checked; callbacks.markDirty(); break;
        }
      });
    });

    // Bind variable editor
    this.bindVariableEditor(el, wf);

    // Bind collapsible section toggles
    bindSectionToggles(el);
  },

  renderVariableRows(vars: Record<string, unknown> | undefined): string {
    if (!vars || Object.keys(vars).length === 0) {
      return '<div class="var-editor-empty">No variables defined</div>';
    }
    return Object.entries(vars).map(([name, value]) => {
      const vtype = this.inferVarType(value);
      const displayVal = vtype === 'object' ? JSON.stringify(value) : String(value ?? '');
      return `
        <div class="var-editor-row" data-var-name="${escapeHtml(name)}">
          <input class="var-input var-col-name mono" value="${escapeHtml(name)}" data-field="name" placeholder="key" />
          <select class="var-select var-col-type" data-field="type">
            <option value="string" ${vtype === 'string' ? 'selected' : ''}>string</option>
            <option value="number" ${vtype === 'number' ? 'selected' : ''}>number</option>
            <option value="boolean" ${vtype === 'boolean' ? 'selected' : ''}>boolean</option>
            <option value="object" ${vtype === 'object' ? 'selected' : ''}>object</option>
          </select>
          ${vtype === 'boolean'
            ? `<label class="var-col-value var-bool-wrap"><input type="checkbox" ${value ? 'checked' : ''} data-field="value" /><span>${value ? 'true' : 'false'}</span></label>`
            : `<input class="var-input var-col-value mono" value="${escapeHtml(displayVal)}" data-field="value" placeholder="${vtype === 'object' ? '{}' : ''}" />`
          }
          <button class="var-row-del" data-field="delete" title="Remove">
            <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
          </button>
        </div>`;
    }).join('');
  },

  inferVarType(value: unknown): string {
    if (value === null || value === undefined) return 'string';
    if (typeof value === 'boolean') return 'boolean';
    if (typeof value === 'number') return 'number';
    if (typeof value === 'object') return 'object';
    return 'string';
  },

  parseVarValue(raw: string, type: string): unknown {
    switch (type) {
      case 'number': return parseFloat(raw) || 0;
      case 'boolean': return raw === 'true';
      case 'object': try { return JSON.parse(raw); } catch { return {}; }
      default: return raw;
    }
  },

  bindVariableEditor(el: HTMLElement, wf: WorkflowData) {
    // Add variable
    el.querySelector('#varEditorAdd')?.addEventListener('click', () => {
      if (!wf.defaultVariables) wf.defaultVariables = {};
      let name = 'newVar';
      let i = 1;
      while (name in wf.defaultVariables) { name = `newVar${++i}`; }
      wf.defaultVariables[name] = '';
      callbacks.markDirty();
      this.refreshVariableEditor(el, wf);
    });

    this.bindVariableRows(el, wf);
  },

  bindVariableRows(el: HTMLElement, wf: WorkflowData) {
    el.querySelectorAll('.var-editor-row').forEach(row => {
      const rowEl = row as HTMLElement;
      const origName = rowEl.dataset.varName!;

      // Delete
      rowEl.querySelector('[data-field="delete"]')?.addEventListener('click', () => {
        if (wf.defaultVariables) delete wf.defaultVariables[origName];
        callbacks.markDirty();
        this.refreshVariableEditor(el, wf);
      });

      // Name change
      const nameInput = rowEl.querySelector('[data-field="name"]') as HTMLInputElement | null;
      nameInput?.addEventListener('change', () => {
        const newName = nameInput.value.trim();
        if (!newName || !wf.defaultVariables) return;
        if (newName !== origName) {
          const val = wf.defaultVariables[origName];
          delete wf.defaultVariables[origName];
          wf.defaultVariables[newName] = val;
          callbacks.markDirty();
          this.refreshVariableEditor(el, wf);
        }
      });

      // Type change
      const typeSelect = rowEl.querySelector('[data-field="type"]') as HTMLSelectElement | null;
      typeSelect?.addEventListener('change', () => {
        if (!wf.defaultVariables) return;
        const oldVal = wf.defaultVariables[origName];
        const newType = typeSelect.value;
        // Convert value to new type
        switch (newType) {
          case 'string': wf.defaultVariables[origName] = String(oldVal ?? ''); break;
          case 'number': wf.defaultVariables[origName] = Number(oldVal) || 0; break;
          case 'boolean': wf.defaultVariables[origName] = Boolean(oldVal); break;
          case 'object': wf.defaultVariables[origName] = typeof oldVal === 'object' ? oldVal : {}; break;
        }
        callbacks.markDirty();
        this.refreshVariableEditor(el, wf);
      });

      // Value change
      const valInput = rowEl.querySelector('[data-field="value"]') as HTMLInputElement | null;
      if (valInput) {
        const event = valInput.type === 'checkbox' ? 'change' : 'input';
        valInput.addEventListener(event, () => {
          if (!wf.defaultVariables) return;
          const type = typeSelect?.value || 'string';
          if (valInput.type === 'checkbox') {
            wf.defaultVariables[origName] = valInput.checked;
            const label = valInput.closest('.var-bool-wrap')?.querySelector('span');
            if (label) label.textContent = valInput.checked ? 'true' : 'false';
          } else {
            wf.defaultVariables[origName] = this.parseVarValue(valInput.value, type);
          }
          callbacks.markDirty();
        });
      }
    });
  },

  refreshVariableEditor(el: HTMLElement, wf: WorkflowData) {
    const rowsEl = el.querySelector('#varEditorRows');
    if (rowsEl) {
      rowsEl.innerHTML = this.renderVariableRows(wf.defaultVariables);
      this.bindVariableRows(el, wf);
    }
  },

  workflowRunHtml(): string {
    const run = State.activeRun;
    if (!run) return '';
    const statusClass = run.status.toLowerCase();
    const fmtTime = (iso?: string) => {
      if (!iso) return '—';
      try { return new Date(iso).toLocaleTimeString('en-US', { hour12: false }); }
      catch { return iso; }
    };
    const fmtDuration = (dur?: string) => {
      if (!dur) return '—';
      const m = dur.match(/PT(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?/);
      if (!m) return dur;
      const s = parseFloat(m[3] || '0');
      if (s < 1) return `${Math.round(s * 1000)}ms`;
      return `${s.toFixed(2)}s`;
    };
    const renderVars = (vars?: Record<string, unknown>) => {
      if (!vars || Object.keys(vars).length === 0) return '';
      return Object.entries(vars)
        .filter(([k]) => !k.startsWith('__'))
        .map(([k, v]) => `<div class="ins-runtime-row"><span class="ins-runtime-key">${escapeHtml(k)}</span><span class="ins-runtime-val">${escapeHtml(typeof v === 'object' ? JSON.stringify(v) : String(v ?? ''))}</span></div>`)
        .join('');
    };
    const varsHtml = renderVars(run.globalVariables);
    return `
    <div class="ins-section">
      <div class="ins-section-title">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/></svg>
        Run ${escapeHtml(run.runId.substring(0, 8))}…
      </div>
      <div class="ins-runtime-card ${statusClass}">
        <div class="ins-runtime-row"><span class="ins-runtime-key">Status</span><span class="ins-runtime-val"><strong>${escapeHtml(run.status)}</strong></span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">Start</span><span class="ins-runtime-val">${fmtTime(run.startTime)}</span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">End</span><span class="ins-runtime-val">${fmtTime(run.endTime)}</span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">Duration</span><span class="ins-runtime-val">${fmtDuration(run.duration)}</span></div>
      </div>
      ${varsHtml ? `<div class="ins-runtime-data-section"><div class="ins-runtime-data-label">Final Variables</div>${varsHtml}</div>` : ''}
    </div>`;
  },

  nodeRuntimeHtml(node: FlowNode): string {
    const d = node._runData;
    if (!d) return '';
    const statusClass = (d.status as string || '').toLowerCase();
    const fmtTime = (iso?: unknown) => {
      if (!iso || typeof iso !== 'string') return '—';
      try { return new Date(iso).toLocaleTimeString('en-US', { hour12: false }); }
      catch { return String(iso); }
    };
    const fmtDuration = (dur?: unknown) => {
      if (!dur || typeof dur !== 'string') return '—';
      const m = (dur as string).match(/PT(?:(\d+)H)?(?:(\d+)M)?(?:([\d.]+)S)?/);
      if (!m) return dur as string;
      const s = parseFloat(m[3] || '0');
      if (s < 1) return `${Math.round(s * 1000)}ms`;
      return `${s.toFixed(2)}s`;
    };
    const renderDataTable = (label: string, data: unknown) => {
      if (!data || typeof data !== 'object') return '';
      const entries = Object.entries(data as Record<string, unknown>);
      if (entries.length === 0) return '';
      return `<div class="ins-runtime-data-section">
        <div class="ins-runtime-data-label">${escapeHtml(label)}</div>
        ${entries.map(([k, v]) => {
          const valStr = typeof v === 'object' ? JSON.stringify(v) : String(v ?? '');
          return `<div class="ins-runtime-row"><span class="ins-runtime-key">${escapeHtml(k)}</span><span class="ins-runtime-val">${escapeHtml(valStr)}</span></div>`;
        }).join('')}
      </div>`;
    };
    return `
    <div class="ins-section">
      <div class="ins-section-title">
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/></svg>
        Execution
      </div>
      <div class="ins-runtime-card ${statusClass}">
        <div class="ins-runtime-row"><span class="ins-runtime-key">Status</span><span class="ins-runtime-val"><strong>${escapeHtml(String(d.status || '—'))}</strong></span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">Start</span><span class="ins-runtime-val">${fmtTime(d.startTime)}</span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">End</span><span class="ins-runtime-val">${fmtTime(d.endTime)}</span></div>
        <div class="ins-runtime-row"><span class="ins-runtime-key">Duration</span><span class="ins-runtime-val">${fmtDuration(d.duration)}</span></div>
        ${d.errorMessage ? `<div class="ins-runtime-row"><span class="ins-runtime-key">Error</span><span class="ins-runtime-val" style="color:var(--err)">${escapeHtml(String(d.errorMessage))}</span></div>` : ''}
        ${d.cancelReason ? `<div class="ins-runtime-row"><span class="ins-runtime-key">Reason</span><span class="ins-runtime-val">${escapeHtml(String(d.cancelReason))}</span></div>` : ''}
        ${d.nodeInstanceId ? `<div class="ins-runtime-row"><span class="ins-runtime-key">Instance</span><span class="ins-runtime-val" style="font-size:9px">${escapeHtml(String(d.nodeInstanceId).substring(0, 8))}…</span></div>` : ''}
      </div>
      ${(() => {
        // For Log nodes, show resolved log message prominently
        const rt = node.runtimeType?.toLowerCase();
        const out = d.outputData as Record<string, unknown> | undefined;
        if (rt === 'log' && out?.message) {
          const lvl = String(out.level || 'info').toUpperCase();
          const lvlColor = lvl === 'ERROR' ? 'var(--err)' : lvl === 'WARN' || lvl === 'WARNING' ? 'var(--warn, orange)' : 'var(--ok, #4caf50)';
          return `<div class="ins-runtime-data-section">
            <div class="ins-runtime-data-label">Log Output</div>
            <div style="padding:6px 8px; border-radius:4px; background:var(--bg-2); border-left:3px solid ${lvlColor}; font-size:12px;">
              <span style="font-weight:600; color:${lvlColor}; margin-right:6px;">${escapeHtml(lvl)}</span>
              <span>${escapeHtml(String(out.message))}</span>
            </div>
          </div>`;
        }
        return '';
      })()}
      ${renderDataTable('Input Data', d.inputData)}
      ${renderDataTable('Output Data', d.outputData)}
      ${renderDataTable('Global Variables', d.globalVariables)}
    </div>`;
  },

  renderEdge(id: string) {
    const edge = State.edges.get(id);
    if (!edge) return;
    const src = State.nodes.get(edge.sourceNodeId);
    const tgt = State.nodes.get(edge.targetNodeId);
    const el = document.getElementById('inspector')!;

    // Build runtime data section if available
    let runtimeHtml = '';
    if (edge._runtimeData) {
      const d = edge._runtimeData;
      const msgType = d.messageType as string || 'Unknown';
      const statusClass = (d.status as string || '').toLowerCase();
      runtimeHtml = `
      <div class="ins-section">
        <div class="ins-section-title">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/></svg>
          Runtime Data
        </div>
        <div class="ins-runtime-card ${statusClass}">
          <div class="ins-runtime-type">${escapeHtml(msgType)}</div>
          ${Object.entries(d)
            .filter(([k]) => k !== 'messageType')
            .map(([k, v]) => `<div class="ins-runtime-row"><span class="ins-runtime-key">${escapeHtml(k)}</span><span class="ins-runtime-val">${escapeHtml(String(v ?? '—'))}</span></div>`)
            .join('')}
        </div>
      </div>`;
    }

    el.innerHTML = `
      <div class="ins-header">
        <div class="ins-title">
          <div class="ins-title-text">Edge</div>
        </div>
        <div class="ins-subtitle mono">${escapeHtml(src?.nodeName || '?')} → ${escapeHtml(tgt?.nodeName || '?')}</div>
      </div>
      <div class="ins-section">
        <div class="ins-section-title">Connection</div>
        <div class="ins-field">
          <label class="ins-field-label">Source port</label>
          <input readonly value="${escapeHtml(edge.sourcePort + (edge.sourcePortName ? ':' + edge.sourcePortName : ''))}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Target</label>
          <input readonly value="${escapeHtml((tgt?.nodeName || '?') + ' (in)')}" />
        </div>
      </div>
      ${runtimeHtml}
      <div class="ins-section">
        <button class="tb-btn danger" data-action="delete-edge" data-edge-id="${id}" style="width:100%; justify-content:center; background:var(--bg-3); padding: 8px; border-radius:6px;">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" width="14" height="14"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>
          Delete edge
        </button>
      </div>
    `;
    this.bindInspectorActions(el);
    bindSectionToggles(el);
  },

  renderNode(id: string) {
    const node = State.nodes.get(id);
    if (!node) return;
    const type = NodeTypes[node.runtimeType];
    if (!type) return;
    const el = document.getElementById('inspector')!;

    // Pre-load input params for AI nodes (this node or upstream AI nodes)
    const aiNodesToFetch: { mode: string; name: string }[] = [];

    if (node.runtimeType.toLowerCase() === 'ai') {
      const mode = String(node.config?.mode || 'skill').toLowerCase();
      const name = mode === 'skill' ? String(node.config?.skillName || '') : String(node.config?.subAgentName || '');
      if (name && cachedInputParamsKey !== `${mode}:${name}`) {
        aiNodesToFetch.push({ mode, name });
      }
    }

    // Also prefetch metadata for upstream AI nodes so inbound fields are available
    State.edges.forEach(edge => {
      if (edge.targetNodeId !== id) return;
      const srcNode = State.nodes.get(edge.sourceNodeId);
      if (!srcNode || srcNode.runtimeType.toLowerCase() !== 'ai') return;
      const mode = String(srcNode.config?.mode || 'skill').toLowerCase();
      const name = mode === 'skill' ? String(srcNode.config?.skillName || '') : String(srcNode.config?.subAgentName || '');
      if (!name) return;
      const key = `${mode}:${name}`;
      const cached = (window as Record<string, unknown>).__cachedAIOutputs as Record<string, unknown> | undefined;
      if (!cached?.[key]) {
        aiNodesToFetch.push({ mode, name });
      }
    });

    if (aiNodesToFetch.length > 0) {
      Promise.all(aiNodesToFetch.map(a => loadInputParams(a.mode, a.name)))
        .then(() => {
          // Re-render after fetches complete to update inbound fields
          this.renderNode(id);
        });
      // If we're fetching, still render with what we have now (will re-render after fetch)
      if (aiNodesToFetch.some(a => {
        const cached = (window as Record<string, unknown>).__cachedAIOutputs as Record<string, unknown> | undefined;
        return !cached?.[`${a.mode}:${a.name}`];
      })) {
        // First render continues below — will re-render when fetch completes
      }
    }

    // Prefetch server workflows for subflow dropdown
    if (node.runtimeType.toLowerCase() === 'subflow' && !serverWorkflowsLoaded) {
      ensureServerWorkflows().then(() => this.renderNode(id));
    }

    const configHtml = this.configFieldsHtml(node);

    el.innerHTML = `
      <div class="ins-header">
        <div class="ins-title">
          <div class="node-icon" style="color: ${type.color}; width: 24px; height: 24px;">${type.icon}</div>
          <div>
            <div class="ins-title-text">${escapeHtml(node.nodeName || type.label)}</div>
            <div class="ins-subtitle mono">${node.runtimeType}</div>
          </div>
        </div>
      </div>
      ${this.nodeRuntimeHtml(node)}
      <div class="ins-section">
        <div class="ins-section-title">General</div>
        <div class="ins-field">
          <label class="ins-field-label">Name</label>
          <input value="${escapeHtml(node.nodeName || '')}" data-bind="name" data-node-id="${id}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Node ID</label>
          <input readonly value="${id}" class="mono" />
        </div>
      </div>
      ${configHtml}
      <div class="ins-section">
        <div class="ins-section-title">Ports</div>
        <div class="ins-field-hint">
          ${type.ports.in ? '• <strong>in</strong> — accepts input<br>' : ''}
          ${type.ports.out && !type.branches ? '• <strong>onFinish</strong> — output<br>' : ''}
          ${type.branches ? '• <strong>branches</strong>: ' + type.branches.map(b => '<code>' + escapeHtml(b) + '</code>').join(', ') + '<br>' : ''}
          ${type.ports.error ? '• <strong>onError</strong> — error output<br>' : ''}
        </div>
      </div>
      <div class="ins-section">
        <button class="tb-btn danger" data-action="delete-node" data-node-id="${id}" style="width:100%; justify-content:center; background:var(--bg-3); padding: 8px; border-radius:6px;">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" width="14" height="14"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>
          Delete node
        </button>
      </div>
    `;
    this.bindInspectorActions(el);
    bindSectionToggles(el);
    this.mountCodeEditors(el, node);
  },

  mountCodeEditors(el: HTMLElement, node: FlowNode) {
    // Find Monaco containers and mount editors
    el.querySelectorAll<HTMLElement>('.ins-monaco-container').forEach(container => {
      const nodeId = container.dataset.nodeId;
      const configKey = container.dataset.configKey;
      const lang = container.dataset.lang;
      if (!nodeId || !configKey) return;
      const value = String(node.config?.[configKey] ?? '');
      if (lang === 'json') {
        mountJsonEditor(container.id, value, (val) => {
          callbacks.updateNodeConfig(nodeId, configKey, val);
        });
      } else if (lang === 'template') {
        const fields = discoverInboundFields(nodeId);
        mountTemplateEditor(container.id, value, fields, (val) => {
          callbacks.updateNodeConfig(nodeId, configKey, val);
        });
      } else {
        mountCSharpEditor(container.id, value, (val) => {
          callbacks.updateNodeConfig(nodeId, configKey, val);
        });
      }
    });
  },

  bindInspectorActions(el: HTMLElement) {
    // Name binding
    el.querySelectorAll<HTMLInputElement>('[data-bind="name"]').forEach(input => {
      input.addEventListener('input', () => {
        callbacks.updateNode(input.dataset.nodeId!, { nodeName: input.value } as Partial<FlowNode>);
      });
    });
    // Config bindings
    el.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>('[data-config-key]').forEach(input => {
      const event = input.tagName === 'SELECT' ? 'change' : 'input';
      input.addEventListener(event, () => {
        const nodeId = input.dataset.nodeId!;
        const key = input.dataset.configKey!;
        let value: unknown = input.value;
        if (input.type === 'number' || input.type === 'range') value = parseFloat(input.value);
        if (input.type === 'checkbox') value = (input as HTMLInputElement).checked;
        callbacks.updateNodeConfig(nodeId, key, value);
      });
    });
    // Delete node
    el.querySelectorAll('[data-action="delete-node"]').forEach(btn => {
      btn.addEventListener('click', () => {
        callbacks.deleteNode((btn as HTMLElement).dataset.nodeId!);
      });
    });
    // Delete edge
    el.querySelectorAll('[data-action="delete-edge"]').forEach(btn => {
      btn.addEventListener('click', () => {
        callbacks.deleteEdge((btn as HTMLElement).dataset.edgeId!);
      });
    });
    // Add branch
    el.querySelectorAll('[data-action="add-branch"]').forEach(btn => {
      btn.addEventListener('click', () => {
        callbacks.addBranch((btn as HTMLElement).dataset.nodeId!);
      });
    });
    // Remove branch
    el.querySelectorAll('[data-action="remove-branch"]').forEach(btn => {
      const b = btn as HTMLElement;
      btn.addEventListener('click', () => {
        callbacks.removeBranch(b.dataset.nodeId!, parseInt(b.dataset.branchIndex || '0'));
      });
    });
    // Rename branch
    el.querySelectorAll<HTMLInputElement>('[data-branch-rename]').forEach(input => {
      input.addEventListener('input', () => {
        callbacks.renameBranch(input.dataset.nodeId!, parseInt(input.dataset.branchIndex || '0'), input.value);
      });
    });
    // Toggle collapse
    el.querySelectorAll('[data-action="toggle-collapse-insp"]').forEach(btn => {
      btn.addEventListener('click', () => {
        callbacks.toggleCollapse((btn as HTMLElement).dataset.nodeId!);
      });
    });
    // Update node (for select-based config that updates full config object)
    el.querySelectorAll<HTMLSelectElement>('[data-node-update]').forEach(select => {
      select.addEventListener('change', () => {
        const nodeId = select.dataset.nodeId!;
        const node = State.nodes.get(nodeId);
        if (!node) return;
        const key = select.dataset.nodeUpdate!;
        callbacks.updateNodeConfig(nodeId, key, select.value);
      });
    });
    // Trigger param: add
    el.querySelectorAll('[data-action="add-trigger-param"]').forEach(btn => {
      btn.addEventListener('click', () => {
        const nodeId = (btn as HTMLElement).dataset.nodeId!;
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let params: { name: string; value: string }[] = [];
        try { params = JSON.parse(String(node.config.params || '[]')); } catch { /* ignore */ }
        params.push({ name: '', value: '' });
        callbacks.updateNodeConfig(nodeId, 'params', JSON.stringify(params));
        this.render();
      });
    });
    // Trigger param: remove
    el.querySelectorAll('[data-action="remove-trigger-param"]').forEach(btn => {
      btn.addEventListener('click', () => {
        const b = btn as HTMLElement;
        const nodeId = b.dataset.nodeId!;
        const idx = parseInt(b.dataset.paramIndex || '0');
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let params: { name: string; value: string }[] = [];
        try { params = JSON.parse(String(node.config.params || '[]')); } catch { /* ignore */ }
        params.splice(idx, 1);
        callbacks.updateNodeConfig(nodeId, 'params', JSON.stringify(params));
        this.render();
      });
    });
    // Trigger param: edit name/value
    el.querySelectorAll<HTMLInputElement>('[data-param-name]').forEach(input => {
      input.addEventListener('input', () => {
        const nodeId = input.dataset.nodeId!;
        const idx = parseInt(input.dataset.paramIndex || '0');
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let params: { name: string; value: string }[] = [];
        try { params = JSON.parse(String(node.config.params || '[]')); } catch { /* ignore */ }
        if (params[idx]) params[idx].name = input.value;
        callbacks.updateNodeConfig(nodeId, 'params', JSON.stringify(params));
      });
    });
    el.querySelectorAll<HTMLInputElement>('[data-param-value]').forEach(input => {
      input.addEventListener('input', () => {
        const nodeId = input.dataset.nodeId!;
        const idx = parseInt(input.dataset.paramIndex || '0');
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let params: { name: string; value: string }[] = [];
        try { params = JSON.parse(String(node.config.params || '[]')); } catch { /* ignore */ }
        if (params[idx]) params[idx].value = input.value;
        callbacks.updateNodeConfig(nodeId, 'params', JSON.stringify(params));
      });
    });
    // Cron preset buttons
    el.querySelectorAll<HTMLButtonElement>('.ins-cron-preset').forEach(btn => {
      btn.addEventListener('click', () => {
        const nodeId = btn.dataset.nodeId!;
        const preset = btn.dataset.preset!;
        callbacks.updateNodeConfig(nodeId, 'schedule', preset);
        const node = State.nodes.get(nodeId);
        if (node) {
          this.validateStartNode(node);
          Nodes.render();
        }
        this.render();
      });
    });
    // Local validation on triggerType change — re-render inspector to show/hide fields
    el.querySelectorAll<HTMLSelectElement>('[data-config-key="triggerType"]').forEach(select => {
      select.addEventListener('change', () => {
        const nodeId = select.dataset.nodeId!;
        const node = State.nodes.get(nodeId);
        if (node && node.runtimeType.toLowerCase() === 'start') {
          this.validateStartNode(node);
          Nodes.render();
        }
        setTimeout(() => this.render(), 50);
      });
    });
    // Re-render inspector when AI mode changes to show/hide mode-specific fields
    el.querySelectorAll<HTMLSelectElement>('[data-config-key="mode"]').forEach(select => {
      const nodeId = select.dataset.nodeId;
      const node = nodeId ? State.nodes.get(nodeId) : null;
      if (node && node.runtimeType.toLowerCase() === 'ai') {
        select.addEventListener('change', () => {
          debouncedNodeRender();
          setTimeout(() => this.render(), 50);
        });
      }
    });
    // Re-render when AI skill/agent selection changes to load input params
    el.querySelectorAll<HTMLSelectElement>('[data-config-key="skillName"], [data-config-key="subAgentName"]').forEach(select => {
      const nodeId = select.dataset.nodeId;
      const node = nodeId ? State.nodes.get(nodeId) : null;
      if (node && node.runtimeType.toLowerCase() === 'ai') {
        select.addEventListener('change', async () => {
          const mode = String(node.config?.mode || 'skill').toLowerCase();
          await loadInputParams(mode, select.value);
          setTimeout(() => this.render(), 50);
        });
      }
    });
    // AI input binding — mode tab clicks (None / Field / Static)
    el.querySelectorAll<HTMLButtonElement>('[data-ai-mode]').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        const nodeId = btn.dataset.nodeId!;
        const paramName = btn.dataset.aiParam!;
        const mode = btn.dataset.aiMode!;
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let bindings: Record<string, string> = {};
        try {
          bindings = typeof node.config?.inputBindings === 'string'
            ? JSON.parse(node.config.inputBindings as string)
            : (node.config?.inputBindings as Record<string, string>) || {};
        } catch { /* ignore */ }

        if (mode === 'none') {
          delete bindings[paramName];
        } else if (mode === 'static') {
          bindings[paramName] = 'static:';
        } else if (mode === 'field') {
          bindings[paramName] = 'field:';
        }
        callbacks.updateNodeConfig(nodeId, 'inputBindings', JSON.stringify(bindings));
        // Re-render inspector to reflect the new mode
        this.renderNode(nodeId);
      });
    });
    // AI input binding — field select change
    el.querySelectorAll<HTMLSelectElement>('[data-ai-binding-key]').forEach(select => {
      select.addEventListener('change', () => {
        const nodeId = select.dataset.nodeId!;
        const bindingKey = select.dataset.aiBindingKey!;
        const node = State.nodes.get(nodeId);
        if (!node) return;
        let bindings: Record<string, string> = {};
        try {
          bindings = typeof node.config?.inputBindings === 'string'
            ? JSON.parse(node.config.inputBindings as string)
            : (node.config?.inputBindings as Record<string, string>) || {};
        } catch { /* ignore */ }
        bindings[bindingKey] = select.value;
        callbacks.updateNodeConfig(nodeId, 'inputBindings', JSON.stringify(bindings));
      });
    });
    // AI static value inputs
    el.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-ai-static-key]').forEach(input => {
      const isBool = input.dataset.isBool === '1';
      if (isBool) {
        input.addEventListener('change', () => {
          const nodeId = input.dataset.nodeId!;
          const key = input.dataset.aiStaticKey!;
          const node = State.nodes.get(nodeId);
          if (!node) return;
          let bindings: Record<string, string> = {};
          try {
            bindings = typeof node.config?.inputBindings === 'string'
              ? JSON.parse(node.config.inputBindings as string)
              : (node.config?.inputBindings as Record<string, string>) || {};
          } catch { /* ignore */ }
          const checked = (input as HTMLInputElement).checked;
          bindings[key] = `static:${checked}`;
          callbacks.updateNodeConfig(nodeId, 'inputBindings', JSON.stringify(bindings));
          // Update label
          const label = input.closest('.binding-toggle')?.querySelector('.binding-toggle-label');
          if (label) label.textContent = String(checked);
        });
      } else {
        input.addEventListener('input', () => {
          const nodeId = input.dataset.nodeId!;
          const key = input.dataset.aiStaticKey!;
          const node = State.nodes.get(nodeId);
          if (!node) return;
          let bindings: Record<string, string> = {};
          try {
            bindings = typeof node.config?.inputBindings === 'string'
              ? JSON.parse(node.config.inputBindings as string)
              : (node.config?.inputBindings as Record<string, string>) || {};
          } catch { /* ignore */ }
          bindings[key] = `static:${input.value}`;
          callbacks.updateNodeConfig(nodeId, 'inputBindings', JSON.stringify(bindings));
        });
      }
    });
    // Local validation on schedule input — update badge + friendly text in-place, no re-render
    el.querySelectorAll<HTMLInputElement>('[data-config-key="schedule"]').forEach(input => {
      input.addEventListener('input', () => {
        const nodeId = input.dataset.nodeId!;
        const node = State.nodes.get(nodeId);
        if (node && node.runtimeType.toLowerCase() === 'start') {
          this.validateStartNode(node);
          this.updateCronDisplayInPlace(el, String(node.config.schedule || ''));
          debouncedNodeRender();
        }
      });
    });
    // Log node: parameter binding inputs
    el.querySelectorAll<HTMLInputElement>('[data-binding-key]').forEach(input => {
      input.addEventListener('input', () => {
        const nodeId = input.dataset.nodeId!;
        const bindingKey = input.dataset.bindingKey!;
        const node = State.nodes.get(nodeId);
        if (!node) return;
        // Parse existing bindings
        let bindings: Record<string, string> = {};
        try {
          bindings = typeof node.config.parameterBindings === 'string'
            ? JSON.parse(node.config.parameterBindings as string)
            : (node.config.parameterBindings as unknown as Record<string, string>) || {};
        } catch { /* ignore */ }
        bindings[bindingKey] = input.value;
        callbacks.updateNodeConfig(nodeId, 'parameterBindings', JSON.stringify(bindings));
      });
    });
    // Log node: re-render inspector when messageTemplate changes (to show/hide bindings)
    el.querySelectorAll<HTMLTextAreaElement>('[data-config-key="messageTemplate"]').forEach(input => {
      input.addEventListener('input', () => {
        // Debounce the re-render to avoid losing focus
        debouncedNodeRender();
      });
    });
    // Inbound field click → insert into first empty binding or last focused binding input
    el.querySelectorAll<HTMLElement>('.inbound-field').forEach(chip => {
      chip.addEventListener('click', () => {
        const fieldPath = chip.dataset.fieldPath!;
        const nodeId = chip.dataset.nodeId!;
        // Find the first empty binding input, or the last one
        const bindingInputs = el.querySelectorAll<HTMLInputElement>('.binding-input');
        let targetInput: HTMLInputElement | null = null;
        for (const inp of bindingInputs) {
          if (!inp.value.trim()) { targetInput = inp; break; }
        }
        if (!targetInput && bindingInputs.length > 0) {
          targetInput = bindingInputs[bindingInputs.length - 1];
        }
        if (targetInput) {
          targetInput.value = fieldPath;
          targetInput.dispatchEvent(new Event('input', { bubbles: true }));
          targetInput.focus();
        }
      });
    });
  },

  /** Update the cron friendly text and validation indicator without full re-render */
  updateCronDisplayInPlace(inspectorEl: HTMLElement, schedule: string) {
    // Update friendly text
    const friendlyEl = inspectorEl.querySelector('.ins-cron-friendly') as HTMLElement | null;
    const desc = this.describeCron(schedule);
    if (friendlyEl) {
      friendlyEl.textContent = desc;
      friendlyEl.style.display = desc ? '' : 'none';
    }
    // Update validation indicator in label
    const errSpan = inspectorEl.querySelector('.ins-validation-err') as HTMLElement | null;
    const okSpan = inspectorEl.querySelector('.ins-validation-ok') as HTMLElement | null;
    const cronErr = this.validateCron(schedule);
    if (cronErr) {
      if (errSpan) { errSpan.style.display = ''; errSpan.setAttribute('data-tooltip', cronErr); }
      if (okSpan) okSpan.style.display = 'none';
    } else {
      if (errSpan) errSpan.style.display = 'none';
      if (okSpan) okSpan.style.display = '';
    }
  },

  /** Run local validation on a Start node and update its validationErrors */
  validateStartNode(node: FlowNode) {
    const errors: string[] = [];
    const c = node.config || {};
    if (String(c.triggerType) === 'Schedule') {
      const cronErr = this.validateCron(String(c.schedule || ''));
      if (cronErr) errors.push(cronErr);
    }
    if (String(c.triggerType) === 'Event') {
      if (!c.eventName || !String(c.eventName).trim()) {
        errors.push('Event name is required when trigger type is Event');
      }
    }
    if (String(c.triggerType) === 'WebHook') {
      if (!c.webHookRoute || !String(c.webHookRoute).trim()) {
        errors.push('Route is required when trigger type is WebHook');
      }
    }
    node.validationErrors = errors.length > 0 ? errors : undefined;
  },

  configFieldsHtml(node: FlowNode): string {
    const inboundFields = discoverInboundFields(node.nodeId);
    const ctx: InspectorContext = {
      node,
      id: node.nodeId,
      c: node.config || {},
      escapeHtml,
      inboundFields,
      renderInboundFields: (f, nid, p, b) => this.renderInboundFields(f, nid, p, b),
      describeCron: (cron) => this.describeCron(cron),
      validateCron: (cron) => this.validateCron(cron),
      ai: { skills: cachedSkills, agents: cachedAgents, inputParams: cachedInputParams, inputParamsKey: cachedInputParamsKey, models: aiModels, tierColor },
      workflows: State.workflows,
      serverWorkflows: cachedServerWorkflows,
      currentWfId: State.currentWfId || undefined,
      nodeType: NodeTypes[node.runtimeType] as InspectorContext['nodeType'],
    };

    const result = renderNodeConfig(ctx);

    // Auto-append generic inbound fields panel
    const type = NodeTypes[node.runtimeType];
    let inboundHtml = '';
    if (result.includeInboundPanel && type?.ports?.in && inboundFields.length > 0) {
      inboundHtml = `
        <div class="ins-field">
          <label class="ins-field-label">Available fields</label>
          <div class="inbound-fields-panel">
            ${this.renderInboundFields(inboundFields, node.nodeId, [], {})}
          </div>
        </div>`;
    }

    return insSection(result.title, result.html + inboundHtml);
  },

  /** Human-readable description of a cron expression */
  describeCron(cron: string): string {
    if (!cron.trim()) return '';
    const parts = cron.trim().split(/\s+/);
    if (parts.length < 5) return 'Invalid format';
    const [min, hour, dom, mon, dow] = parts;
    const segments: string[] = [];

    // Helper: detect step patterns like */5, 0/5, etc.
    const isStep = (f: string) => f.includes('/');
    const stepVal = (f: string) => {
      const s = f.split('/')[1];
      return s ? parseInt(s) : 1;
    };

    // Time part
    if (min === '*' && hour === '*') {
      segments.push('Every minute');
    } else if (isStep(min) && hour === '*') {
      const n = stepVal(min);
      segments.push(n === 1 ? 'Every minute' : `Every ${n} minutes`);
    } else if ((min === '0' || min === '*') && isStep(hour)) {
      const n = stepVal(hour);
      segments.push(n === 1 ? 'Every hour' : `Every ${n} hours`);
    } else if (isStep(min) && isStep(hour)) {
      segments.push(`Every ${stepVal(hour)} hours, every ${stepVal(min)} minutes`);
    } else if (min === '0' && hour === '*') {
      segments.push('Every hour');
    } else if (min !== '*' && hour === '*') {
      segments.push(`At minute ${min} of every hour`);
    } else if (min !== '*' && hour !== '*' && !isStep(hour)) {
      if (hour.includes(',')) {
        segments.push(`At :${min.padStart(2, '0')} on hours ${hour}`);
      } else {
        segments.push(`At ${hour}:${min.padStart(2, '0')}`);
      }
    } else {
      segments.push(`${min} ${hour}`);
    }

    // Date part
    if (dom !== '*') {
      if (isStep(dom)) segments.push(`every ${stepVal(dom)} days`);
      else segments.push(`on day ${dom} of month`);
    }
    if (mon !== '*') {
      const months: Record<string, string> = { '1': 'Jan', '2': 'Feb', '3': 'Mar', '4': 'Apr', '5': 'May', '6': 'Jun', '7': 'Jul', '8': 'Aug', '9': 'Sep', '10': 'Oct', '11': 'Nov', '12': 'Dec' };
      const m = mon.split(',').map(m => months[m] || m).join(', ');
      segments.push(`in ${m}`);
    }
    if (dow !== '*') {
      const days: Record<string, string> = { '0': 'Sun', '1': 'Mon', '2': 'Tue', '3': 'Wed', '4': 'Thu', '5': 'Fri', '6': 'Sat', '7': 'Sun' };
      const d = dow.split(',').map(d => days[d] || d).join(', ');
      segments.push(`on ${d}`);
    }

    return segments.join(', ') || cron;
  },

  /** Validate cron expression locally, returns error string or null */
  validateCron(cron: string): string | null {
    if (!cron.trim()) return 'Schedule is required when trigger type is Schedule';
    const parts = cron.trim().split(/\s+/);
    if (parts.length < 5 || parts.length > 6) return `Expected 5 fields (minute hour day month weekday), got ${parts.length}`;
    const ranges = [
      { name: 'minute', min: 0, max: 59 },
      { name: 'hour', min: 0, max: 23 },
      { name: 'day', min: 1, max: 31 },
      { name: 'month', min: 1, max: 12 },
      { name: 'weekday', min: 0, max: 7 },
    ];
    for (let i = 0; i < 5; i++) {
      const field = parts[i];
      const { name, min, max } = ranges[i];
      if (field === '*') continue;
      // Step patterns: */5 or 0/5
      if (/^\d*\*?\/\d+$/.test(field)) {
        const stepParts = field.split('/');
        const step = parseInt(stepParts[1]);
        if (step < 1 || step > max) return `${name}: step ${step} out of range (1-${max})`;
        const base = stepParts[0];
        if (base && base !== '*') {
          const b = parseInt(base);
          if (isNaN(b) || b < min || b > max) return `${name}: start value "${base}" out of range (${min}-${max})`;
        }
        continue;
      }
      // Check comma-separated values
      for (const v of field.split(',')) {
        const dash = v.split('-');
        for (const d of dash) {
          const n = parseInt(d);
          if (isNaN(n) || n < min || n > max) return `${name}: value "${d}" out of range (${min}-${max})`;
        }
      }
    }
    return null;
  },

  /** Render inbound fields panel with source-colored badges */
  renderInboundFields(fields: InboundField[], nodeId: string, placeholders: string[], bindings: Record<string, string>): string {
    const sourceColors: Record<string, string> = {
      error: 'var(--err)',
      output: 'var(--accent)',
      context: 'var(--text-3)',
    };
    const typeIcons: Record<string, string> = {
      string: 'abc',
      number: '123',
      boolean: '✓/✗',
      datetime: '⏱',
      guid: '#id',
      object: '{ }',
      unknown: '?',
    };

    // Group by source
    const groups = new Map<string, InboundField[]>();
    for (const f of fields) {
      const key = f.source === 'error' ? '⚠ Error data' : f.source === 'context' ? '◈ Context' : `▸ ${f.sourceNode}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(f);
    }

    let html = '';
    for (const [group, gFields] of groups) {
      const color = sourceColors[gFields[0].source] || 'var(--text-3)';
      html += `<div class="inbound-group">
        <div class="inbound-group-label" style="color:${color}">${group}</div>`;
      for (const f of gFields) {
        const isBound = Object.values(bindings).includes(f.path);
        html += `
        <div class="inbound-field ${isBound ? 'bound' : ''}" data-field-path="${escapeHtml(f.path)}" data-node-id="${nodeId}" title="${escapeHtml(f.path)}">
          <span class="inbound-field-type" style="color:${color}">${typeIcons[f.type] || '?'}</span>
          <span class="inbound-field-name mono">${escapeHtml(f.name)}</span>
          ${isBound ? '<span class="inbound-field-bound">✓</span>' : ''}
        </div>`;
      }
      html += `</div>`;
    }
    return html;
  }
};
