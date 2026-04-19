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
      <div class="ins-section">
        <div class="ins-section-title">Properties</div>
        <div class="ins-field">
          <label class="ins-field-label">Name</label>
          <input value="${escapeHtml(wf.name || '')}" data-bind="wf-name" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Author</label>
          <input value="${escapeHtml(wf.author || '')}" placeholder="e.g. your name" data-bind="wf-author" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Version</label>
          <input value="${escapeHtml(wf.version || '')}" placeholder="e.g. 1.0.0" data-bind="wf-version" />
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Purpose</label>
          <textarea placeholder="Describe what this workflow does…" data-bind="wf-purpose">${escapeHtml(wf.purpose || '')}</textarea>
        </div>
      </div>
      <div class="ins-section">
        <div class="ins-section-title">Metadata</div>
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
          <input readonly value="${escapeHtml(wf.id)}" class="mono" />
        </div>
      </div>
    `;

    // Bind editable fields
    el.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('[data-bind]').forEach(input => {
      input.addEventListener('input', () => {
        const field = input.dataset.bind;
        if (!wf) return;
        switch (field) {
          case 'wf-name':
            wf.name = input.value;
            const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
            if (titleInput) titleInput.value = input.value;
            callbacks.markDirty();
            break;
          case 'wf-author': wf.author = input.value; callbacks.markDirty(); break;
          case 'wf-version': wf.version = input.value; callbacks.markDirty(); break;
          case 'wf-purpose': wf.purpose = input.value; callbacks.markDirty(); break;
        }
      });
    });
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
    } else if (node.type === 'trigger') {
      title = 'Webhook';
      // Parse params JSON safely
      let params: { name: string; value: string }[] = [];
      try { params = JSON.parse(String(c.params || '[]')); } catch { /* ignore */ }
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
            <label class="ins-field-label">Route URI</label>
            <input class="mono" value="${escapeHtml(String(c.route || ''))}" placeholder="/webhook/my-hook" data-config-key="route" data-node-id="${id}" />
          </div>
        </div>
        <div class="ins-field">
          <label class="ins-field-label">Parameters</label>
          <div class="switch-cases-list" id="triggerParams_${id}">
            ${params.map((p, i) => `
              <div class="switch-case" style="display:flex; gap:4px; align-items:center;">
                <input value="${escapeHtml(p.name)}" placeholder="name" class="mono" style="flex:1" data-param-name data-node-id="${id}" data-param-index="${i}" />
                <input value="${escapeHtml(p.value)}" placeholder="value" class="mono" style="flex:1" data-param-value data-node-id="${id}" data-param-index="${i}" />
                <button class="switch-case-del" data-action="remove-trigger-param" data-node-id="${id}" data-param-index="${i}">
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
                </button>
              </div>
            `).join('')}
          </div>
          <button class="add-case-btn" data-action="add-trigger-param" data-node-id="${id}">+ Add parameter</button>
          <div class="ins-field-hint">Query or body parameters passed to the webhook.</div>
        </div>
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
    } else if (node.type === 'foreach') {
      title = 'ForEach';
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
          <div class="ins-field-hint">Connect <code>iterate</code> to loop body. The <code>out</code> port fires after all iterations.</div>
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
