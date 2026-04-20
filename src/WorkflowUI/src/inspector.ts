// -----------------------------------------------------------------------
// Inspector panel rendering
// -----------------------------------------------------------------------

import type { FlowNode, AppCallbacks, NodeTypeDefinition } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { escapeHtml } from './utils';
import { Nodes } from './nodes';
import { aiModels, tierColor } from './config';

let callbacks: AppCallbacks;
let nodeRenderTimer: ReturnType<typeof setTimeout> | null = null;
function debouncedNodeRender() {
  if (nodeRenderTimer) clearTimeout(nodeRenderTimer);
  nodeRenderTimer = setTimeout(() => Nodes.render(), 300);
}

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
        if (node && node.type.toLowerCase() === 'start') {
          this.validateStartNode(node);
          Nodes.render();
        }
        setTimeout(() => this.render(), 50);
      });
    });
    // Local validation on schedule input — update badge + friendly text in-place, no re-render
    el.querySelectorAll<HTMLInputElement>('[data-config-key="schedule"]').forEach(input => {
      input.addEventListener('input', () => {
        const nodeId = input.dataset.nodeId!;
        const node = State.nodes.get(nodeId);
        if (node && node.type.toLowerCase() === 'start') {
          this.validateStartNode(node);
          this.updateCronDisplayInPlace(el, String(node.config.schedule || ''));
          debouncedNodeRender();
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
    node.validationErrors = errors.length > 0 ? errors : undefined;
  },

  configFieldsHtml(node: FlowNode): string {
    const c = node.config || {};
    const id = node.id;
    let fields = '';
    let title = 'Configuration';

    if (node.type === 'start') {
      title = 'Start Trigger';
      const trigType = String(c.triggerType || 'Manual');
      const cronDesc = trigType === 'Schedule' && c.schedule ? this.describeCron(String(c.schedule)) : '';
      const cronValid = trigType === 'Schedule' ? this.validateCron(String(c.schedule || '')) : null;

      fields = `
        <div class="ins-field">
          <label class="ins-field-label">Trigger type</label>
          <select data-config-key="triggerType" data-node-id="${id}">
            <option value="Manual" ${trigType === 'Manual' ? 'selected' : ''}>Manual</option>
            <option value="Schedule" ${trigType === 'Schedule' ? 'selected' : ''}>Schedule</option>
            <option value="Event" ${trigType === 'Event' ? 'selected' : ''}>Event</option>
          </select>
        </div>
        ${trigType === 'Schedule' ? `
        <div class="ins-field">
          <label class="ins-field-label">Cron schedule ${cronValid ? '<span class="ins-validation-err" data-tooltip="' + escapeHtml(cronValid) + '">⚠</span>' : '<span class="ins-validation-ok">✓</span>'}</label>
          <input class="mono" value="${escapeHtml(String(c.schedule || ''))}" placeholder="0 */5 * * *" data-config-key="schedule" data-node-id="${id}" />
          ${cronDesc ? `<div class="ins-cron-friendly">${escapeHtml(cronDesc)}</div>` : ''}
          <div class="ins-field-hint">
            Format: minute hour day month weekday<br>
            <code>*</code> = any &nbsp; <code>*/5</code> = every 5 &nbsp; <code>1,15</code> = 1st and 15th
          </div>
          <div class="ins-cron-presets">
            <button class="ins-cron-preset" data-preset="* * * * *" data-node-id="${id}">Every min</button>
            <button class="ins-cron-preset" data-preset="*/5 * * * *" data-node-id="${id}">Every 5m</button>
            <button class="ins-cron-preset" data-preset="0 * * * *" data-node-id="${id}">Hourly</button>
            <button class="ins-cron-preset" data-preset="0 0 * * *" data-node-id="${id}">Daily</button>
            <button class="ins-cron-preset" data-preset="0 0 * * 1" data-node-id="${id}">Weekly</button>
          </div>
        </div>` : ''}
        ${trigType === 'Event' ? `
        <div class="ins-field">
          <label class="ins-field-label">Event name</label>
          <input class="mono" value="${escapeHtml(String(c.eventName || ''))}" placeholder="order.created" data-config-key="eventName" data-node-id="${id}" />
          <div class="ins-field-hint">Name of the external event to listen for.</div>
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
          <select data-config-key="model" data-node-id="${id}" class="ai-model-select">
            ${aiModels.map(m => `<option value="${escapeHtml(m.id)}" ${String(c.model) === m.id ? 'selected' : ''}>${escapeHtml(m.label)}</option>`).join('')}
          </select>
          ${(() => {
            const sel = aiModels.find(m => m.id === String(c.model));
            if (!sel) return '';
            return `<div class="ins-model-badge" style="color: ${tierColor(sel.tier)}">
              <span class="ins-model-tier">${sel.tier}</span>
              <span class="ins-model-cost">${sel.costMultiplier}x</span>
            </div>`;
          })()}
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
      // Try rendering from server-provided property descriptors
      const typeDef = NodeTypes[node.type] as NodeTypeDefinition & { serverProperties?: { name: string; label: string; type: string; required: boolean; default: string | null; placeholder: string | null; options: string[] | null }[] };
      if (typeDef?.serverProperties?.length) {
        fields = typeDef.serverProperties.map(prop => {
          const val = escapeHtml(String(c[prop.name] ?? prop.default ?? ''));
          if (prop.type === 'enum' && prop.options) {
            const opts = prop.options.map(o => `<option ${String(c[prop.name]) === o ? 'selected' : ''} value="${escapeHtml(o)}">${escapeHtml(o)}</option>`).join('');
            return `<div class="ins-field">
              <label class="ins-field-label">${escapeHtml(prop.label)}</label>
              <select data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}">${opts}</select>
            </div>`;
          }
          if (prop.type === 'boolean') {
            return `<div class="ins-field">
              <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
                <input type="checkbox" ${c[prop.name] ? 'checked' : ''} data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" style="width:auto" />
                ${escapeHtml(prop.label)}
              </label>
            </div>`;
          }
          if (prop.type === 'number') {
            return `<div class="ins-field">
              <label class="ins-field-label">${escapeHtml(prop.label)}</label>
              <input type="number" value="${val}" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}" />
            </div>`;
          }
          if (prop.type === 'code' || prop.type === 'expression') {
            return `<div class="ins-field">
              <label class="ins-field-label">${escapeHtml(prop.label)}</label>
              <textarea class="mono" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}">${val}</textarea>
            </div>`;
          }
          if (prop.type === 'map') {
            return `<div class="ins-field">
              <label class="ins-field-label">${escapeHtml(prop.label)}</label>
              <textarea class="mono" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '{}')}">${val}</textarea>
              <div class="ins-field-hint">JSON key-value mapping</div>
            </div>`;
          }
          // Default: string input
          return `<div class="ins-field">
            <label class="ins-field-label">${escapeHtml(prop.label)}${prop.required ? ' *' : ''}</label>
            <input value="${val}" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}" class="${prop.type === 'duration' ? 'mono' : ''}" />
          </div>`;
        }).join('');
      } else {
        fields = `<div class="ins-field-hint">No additional configuration.</div>`;
      }
    }

    return `<div class="ins-section">
      <div class="ins-section-title">${title}</div>
      ${fields}
    </div>`;
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
  }
};
