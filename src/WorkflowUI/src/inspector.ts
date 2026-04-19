// -----------------------------------------------------------------------
// Inspector panel rendering
// -----------------------------------------------------------------------

import type { FlowNode, AppCallbacks } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { escapeHtml } from './utils';

let callbacks: AppCallbacks;

export const Inspector = {
  init(cb: AppCallbacks) { callbacks = cb; },

  render() {
    const el = document.getElementById('inspector');
    if (!el) return;
    const { kind, id } = State.selection;
    if (!kind || !id) {
      el.innerHTML = `<div class="ins-empty">
        <svg class="ins-empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="9"/><path d="M12 8v4l2 2"/></svg>
        <div>Select a node or edge<br>to configure its properties</div>
      </div>`;
      return;
    }
    if (kind === 'edge') { this.renderEdge(id); return; }
    if (kind === 'node') { this.renderNode(id); return; }
  },

  renderEdge(id: string) {
    const edge = State.edges.get(id);
    if (!edge) return;
    const src = State.nodes.get(edge.source);
    const tgt = State.nodes.get(edge.target);
    const el = document.getElementById('inspector')!;
    el.innerHTML = `
      <div class="ins-header">
        <div class="ins-title">
          <div class="ins-title-text">Edge</div>
        </div>
        <div class="ins-subtitle mono">${escapeHtml(src?.name || '?')} → ${escapeHtml(tgt?.name || '?')}</div>
      </div>
      <div class="ins-section">
        <div class="ins-section-title">Connection</div>
        <div class="ins-field">
          <label class="ins-field-label">Source port</label>
          <input readonly value="${escapeHtml(edge.sourcePort + (edge.sourcePortName ? ':' + edge.sourcePortName : ''))}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Target</label>
          <input readonly value="${escapeHtml((tgt?.name || '?') + ' (in)')}" />
        </div>
      </div>
      <div class="ins-section">
        <button class="tb-btn danger" data-action="delete-edge" data-edge-id="${id}" style="width:100%; justify-content:center; background:var(--bg-3); padding: 8px; border-radius:6px;">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" width="14" height="14"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/></svg>
          Delete edge
        </button>
      </div>
    `;
    this.bindInspectorActions(el);
  },

  renderNode(id: string) {
    const node = State.nodes.get(id);
    if (!node) return;
    const type = NodeTypes[node.type];
    if (!type) return;
    const el = document.getElementById('inspector')!;
    const configHtml = this.configFieldsHtml(node);

    el.innerHTML = `
      <div class="ins-header">
        <div class="ins-title">
          <div class="node-icon" style="color: ${type.color}; width: 24px; height: 24px;">${type.icon}</div>
          <div>
            <div class="ins-title-text">${escapeHtml(node.name || type.label)}</div>
            <div class="ins-subtitle mono">${node.type}</div>
          </div>
        </div>
      </div>
      <div class="ins-section">
        <div class="ins-section-title">General</div>
        <div class="ins-field">
          <label class="ins-field-label">Name</label>
          <input value="${escapeHtml(node.name || '')}" data-bind="name" data-node-id="${id}" />
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
  },

  bindInspectorActions(el: HTMLElement) {
    // Name binding
    el.querySelectorAll<HTMLInputElement>('[data-bind="name"]').forEach(input => {
      input.addEventListener('input', () => {
        callbacks.updateNode(input.dataset.nodeId!, { name: input.value } as Partial<FlowNode>);
      });
    });
    // Config bindings
    el.querySelectorAll<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>('[data-config-key]').forEach(input => {
      const event = input.tagName === 'SELECT' ? 'change' : 'input';
      input.addEventListener(event, () => {
        const nodeId = input.dataset.nodeId!;
        const key = input.dataset.configKey!;
        let value: unknown = input.value;
        if (input.type === 'number') value = parseFloat(input.value);
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
  },

  configFieldsHtml(node: FlowNode): string {
    const c = node.config || {};
    const id = node.id;
    let fields = '';
    let title = 'Configuration';

    if (node.type === 'start') {
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Trigger type</label>
          <select data-config-key="triggerType" data-node-id="${id}">
            <option ${c.triggerType === 'manual' ? 'selected' : ''}>manual</option>
            <option ${c.triggerType === 'schedule' ? 'selected' : ''}>schedule</option>
            <option ${c.triggerType === 'event' ? 'selected' : ''}>event</option>
          </select>
        </div>
        ${c.triggerType === 'schedule' ? `
        <div class="ins-field">
          <label class="ins-field-label">Cron expression</label>
          <input class="mono" value="${escapeHtml(String(c.schedule || ''))}" data-config-key="schedule" data-node-id="${id}" />
        </div>` : ''}
      `;
    } else if (node.type === 'if') {
      title = 'Condition';
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Condition expression</label>
          <textarea data-config-key="condition" data-node-id="${id}" placeholder="input.value > 10">${escapeHtml(String(c.condition || ''))}</textarea>
          <div class="ins-field-hint">JavaScript-like expression. Connect <code>true</code> and <code>false</code> branches.</div>
        </div>
      `;
    } else if (node.type === 'switch') {
      title = 'Switch';
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Switch expression</label>
          <textarea data-config-key="expression" data-node-id="${id}" placeholder="input.type">${escapeHtml(String(c.expression || ''))}</textarea>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Cases</label>
          <div class="switch-cases-list">
            ${(node.branches || []).map((b, i) => `
              <div class="switch-case">
                <input value="${escapeHtml(b)}" data-branch-rename data-node-id="${id}" data-branch-index="${i}" class="mono" />
                <button class="switch-case-del" data-action="remove-branch" data-node-id="${id}" data-branch-index="${i}">
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
                </button>
              </div>
            `).join('')}
          </div>
          <button class="add-case-btn" data-action="add-branch" data-node-id="${id}">+ Add case</button>
        </div>
      `;
    } else if (node.type === 'parallel') {
      title = 'Parallel';
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Join mode</label>
          <select data-config-key="joinMode" data-node-id="${id}">
            <option ${c.joinMode === 'all' ? 'selected' : ''} value="all">all — wait for all</option>
            <option ${c.joinMode === 'any' ? 'selected' : ''} value="any">any — first to finish</option>
            <option ${c.joinMode === 'race' ? 'selected' : ''} value="race">race — cancel others</option>
          </select>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Timeout (ms)</label>
          <input type="number" value="${c.timeout || 30000}" data-config-key="timeout" data-node-id="${id}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Branches</label>
          <div class="switch-cases-list">
            ${(node.branches || []).map((b, i) => `
              <div class="switch-case">
                <input value="${escapeHtml(b)}" data-branch-rename data-node-id="${id}" data-branch-index="${i}" class="mono" />
                <button class="switch-case-del" data-action="remove-branch" data-node-id="${id}" data-branch-index="${i}">
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
                </button>
              </div>
            `).join('')}
          </div>
          <button class="add-case-btn" data-action="add-branch" data-node-id="${id}">+ Add branch</button>
        </div>
      `;
    } else if (node.type === 'loop') {
      title = 'Loop';
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Mode</label>
          <select data-config-key="mode" data-node-id="${id}">
            <option ${c.mode === 'foreach' ? 'selected' : ''}>foreach</option>
            <option ${c.mode === 'while' ? 'selected' : ''}>while</option>
            <option ${c.mode === 'times' ? 'selected' : ''}>times</option>
          </select>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">${c.mode === 'while' ? 'Condition' : c.mode === 'times' ? 'Count' : 'Source expression'}</label>
          <input class="mono" value="${escapeHtml(String(c.source || ''))}" data-config-key="source" data-node-id="${id}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Max iterations (safety)</label>
          <input type="number" value="${c.maxIterations || 100}" data-config-key="maxIterations" data-node-id="${id}" />
          <div class="ins-field-hint">Connect <code>iterate</code> to loop body, <code>complete</code> to next step.</div>
        </div>
      `;
    } else if (node.type === 'subflow') {
      title = 'Subflow Reference';
      const wfOptions = Object.values(State.workflows)
        .filter(w => w.id !== State.currentWfId)
        .map(w => `<option value="${w.id}" ${c.workflowId === w.id ? 'selected' : ''}>${escapeHtml(w.name)}</option>`)
        .join('');
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Workflow</label>
          <select data-config-key="workflowId" data-node-id="${id}">
            <option value="">— select a workflow —</option>
            ${wfOptions}
          </select>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Input mapping (JSON)</label>
          <textarea data-config-key="inputMapping" data-node-id="${id}">${escapeHtml(String(c.inputMapping || '{}'))}</textarea>
        </div>
        <div class="ins-field">
          <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
            <input type="checkbox" ${c.waitForCompletion ? 'checked' : ''} data-config-key="waitForCompletion" data-node-id="${id}" style="width:auto" />
            Wait for subflow completion
          </label>
        </div>
      `;
    } else if (node.type === 'ai') {
      title = 'AI Configuration';
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Mode</label>
          <select data-config-key="mode" data-node-id="${id}">
            <option ${c.mode === 'skill' ? 'selected' : ''} value="skill">Skill</option>
            <option ${c.mode === 'subagent' ? 'selected' : ''} value="subagent">Subagent</option>
            <option ${c.mode === 'prompt' ? 'selected' : ''} value="prompt">Raw Prompt</option>
          </select>
        </div>
        ${c.mode === 'skill' ? `
          <div class="ins-field">
            <label class="ins-field-label">Skill ID</label>
            <input class="mono" value="${escapeHtml(String(c.skillId || ''))}" placeholder="e.g. summarize_document" data-config-key="skillId" data-node-id="${id}" />
          </div>
        ` : c.mode === 'subagent' ? `
          <div class="ins-field">
            <label class="ins-field-label">Subagent ID</label>
            <input class="mono" value="${escapeHtml(String(c.subagentId || ''))}" placeholder="e.g. code_reviewer" data-config-key="subagentId" data-node-id="${id}" />
          </div>
        ` : ''}
        <div class="ins-field">
          <label class="ins-field-label">Model</label>
          <select data-config-key="model" data-node-id="${id}">
            <option ${c.model === 'claude-opus-4-7' ? 'selected' : ''}>claude-opus-4-7</option>
            <option ${c.model === 'claude-sonnet-4-6' ? 'selected' : ''}>claude-sonnet-4-6</option>
            <option ${c.model === 'claude-haiku-4-5' ? 'selected' : ''}>claude-haiku-4-5</option>
          </select>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">System prompt</label>
          <textarea data-config-key="systemPrompt" data-node-id="${id}" placeholder="You are a helpful assistant...">${escapeHtml(String(c.systemPrompt || ''))}</textarea>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Input mapping</label>
          <input class="mono" value="${escapeHtml(String(c.inputMapping || 'input'))}" data-config-key="inputMapping" data-node-id="${id}" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Output key</label>
          <input class="mono" value="${escapeHtml(String(c.outputKey || 'result'))}" data-config-key="outputKey" data-node-id="${id}" />
        </div>
        <div class="ins-field" style="display:flex; gap:8px;">
          <div style="flex:1">
            <label class="ins-field-label">Temperature</label>
            <input type="number" step="0.1" min="0" max="2" value="${c.temperature || 0.7}" data-config-key="temperature" data-node-id="${id}" />
          </div>
          <div style="flex:1">
            <label class="ins-field-label">Max tokens</label>
            <input type="number" value="${c.maxTokens || 4096}" data-config-key="maxTokens" data-node-id="${id}" />
          </div>
        </div>
      `;
    } else if (node.type === 'http') {
      fields = `
        <div class="ins-field" style="display:flex; gap:8px;">
          <div style="width: 90px">
            <label class="ins-field-label">Method</label>
            <select data-config-key="method" data-node-id="${id}">
              <option ${c.method === 'GET' ? 'selected' : ''}>GET</option>
              <option ${c.method === 'POST' ? 'selected' : ''}>POST</option>
              <option ${c.method === 'PUT' ? 'selected' : ''}>PUT</option>
              <option ${c.method === 'PATCH' ? 'selected' : ''}>PATCH</option>
              <option ${c.method === 'DELETE' ? 'selected' : ''}>DELETE</option>
            </select>
          </div>
          <div style="flex:1">
            <label class="ins-field-label">URL</label>
            <input class="mono" value="${escapeHtml(String(c.url || ''))}" data-config-key="url" data-node-id="${id}" />
          </div>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Headers (JSON)</label>
          <textarea data-config-key="headers" data-node-id="${id}">${escapeHtml(String(c.headers || '{}'))}</textarea>
        </div>
      `;
    } else if (node.type === 'delay') {
      fields = `
        <div class="ins-field" style="display:flex; gap:8px;">
          <div style="flex:2">
            <label class="ins-field-label">Duration</label>
            <input type="number" value="${c.duration || 1000}" data-config-key="duration" data-node-id="${id}" />
          </div>
          <div style="flex:1">
            <label class="ins-field-label">Unit</label>
            <select data-config-key="unit" data-node-id="${id}">
              <option ${c.unit === 'ms' ? 'selected' : ''}>ms</option>
              <option ${c.unit === 's' ? 'selected' : ''}>s</option>
              <option ${c.unit === 'm' ? 'selected' : ''}>m</option>
            </select>
          </div>
        </div>
      `;
    } else if (node.type === 'transform') {
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Expression</label>
          <textarea data-config-key="expression" data-node-id="${id}">${escapeHtml(String(c.expression || ''))}</textarea>
          <div class="ins-field-hint">Transform the input payload. Reference with <code>input</code>.</div>
        </div>
      `;
    } else if (node.type === 'log') {
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Level</label>
          <select data-config-key="level" data-node-id="${id}">
            <option ${c.level === 'info' ? 'selected' : ''}>info</option>
            <option ${c.level === 'warn' ? 'selected' : ''}>warn</option>
            <option ${c.level === 'error' ? 'selected' : ''}>error</option>
            <option ${c.level === 'debug' ? 'selected' : ''}>debug</option>
          </select>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Message</label>
          <input value="${escapeHtml(String(c.message || ''))}" data-config-key="message" data-node-id="${id}" />
        </div>
      `;
    } else if (node.type === 'container') {
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Label</label>
          <input value="${escapeHtml(String(c.label || 'Group'))}" data-config-key="label" data-node-id="${id}" />
        </div>
        <div class="ins-field">
          <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
            <input type="checkbox" ${node.collapsed ? 'checked' : ''} data-action="toggle-collapse-insp" data-node-id="${id}" style="width:auto" />
            Collapsed
          </label>
        </div>
      `;
    } else if (node.type === 'end') {
      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Exit status</label>
          <select data-config-key="status" data-node-id="${id}">
            <option ${c.status === 'success' ? 'selected' : ''}>success</option>
            <option ${c.status === 'failure' ? 'selected' : ''}>failure</option>
          </select>
        </div>
      `;
    } else {
      fields = `<div class="ins-field-hint">No additional configuration.</div>`;
    }

    return `<div class="ins-section">
      <div class="ins-section-title">${title}</div>
      ${fields}
    </div>`;
  }
};
