// -----------------------------------------------------------------------
// App orchestrator — all node/edge/workflow operations
// -----------------------------------------------------------------------

import type { FlowNode, CreateNodeOptions, CreateEdgeParams, ModalOptions, AppCallbacks, WorkflowData } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { uid, now, clone, ensureChildren, getSlots, toast, escapeHtml } from './utils';
import type { FlowEdge } from './types';

let renderTimer: ReturnType<typeof setTimeout> | null = null;
let validationTimer: ReturnType<typeof setTimeout> | null = null;

function debouncedRender(fn: () => void, ms = 1000) {
  if (renderTimer) clearTimeout(renderTimer);
  renderTimer = setTimeout(fn, ms);
}

function debouncedValidation(fn: () => void, ms = 5000) {
  if (validationTimer) clearTimeout(validationTimer);
  validationTimer = setTimeout(fn, ms);
}
import { Canvas } from './canvas';
import { Minimap } from './minimap';
import { Nodes } from './nodes';
import { Edges } from './edges';
import { Palette } from './palette';
import { Inspector } from './inspector';
import { Exec } from './exec';
import { saveWorkflow as saveWorkflowApi, deleteWorkflowApi, listWorkflows, getWorkflow, fetchNodeTypes, validateWorkflow } from './api';
import { mergeServerNodeTypes } from './registry';

function buildCallbacks(): AppCallbacks {
  return {
    select: (kind, id) => App.select(kind, id),
    createNode: (type, x, y, opts) => App.createNode(type, x, y, opts),
    createChildNode: (type, parentId, slot, index) => App.createChildNode(type, parentId, slot, index),
    insertNodeRelative: (type, refNodeId, position) => App.insertNodeRelative(type, refNodeId, position),
    reparentNode: (nodeId, newParentId, newSlot, index) => App.reparentNode(nodeId, newParentId, newSlot, index),
    updateNode: (id, patch) => App.updateNode(id, patch),
    updateNodeConfig: (id, key, value) => App.updateNodeConfig(id, key, value),
    deleteNode: (id) => App.deleteNode(id),
    deleteEdge: (id) => App.deleteEdge(id),
    toggleCollapse: (id) => App.toggleCollapse(id),
    addBranch: (nodeId) => App.addBranch(nodeId),
    removeBranch: (nodeId, index) => App.removeBranch(nodeId, index),
    renameBranch: (nodeId, index, newName) => App.renameBranch(nodeId, index, newName),
    markDirty: () => App.markDirty(),
    isDescendantOf: (a, b) => App.isDescendantOf(a, b),
    createEdge: (params) => App.createEdge(params),
    renderAll: () => App.renderAll(),
  };
}

export const App = {
  init() {
    const cb = buildCallbacks();
    Canvas.init(cb);
    Canvas.onApply(() => Minimap.render());
    Nodes.init(cb);
    Edges.init(cb);
    Inspector.init(cb);
    Exec.init(
      (level, nodeId, msg) => this.log(level, nodeId, msg),
      () => this.clearLogs(),
    );
    Palette.render();
    this.loadWorkflowsFromStorage();
    this.bindShortcuts();
    this.bindTitleInput();
    this.bindFileInput();
    this.bindTopbarActions();
    this.syncThemeIcon();
    this.renderAll();

    // Load engine node types from server (non-blocking)
    fetchNodeTypes().then(types => {
      mergeServerNodeTypes(types);
      Palette.render();
      this.log('info', null, `Loaded ${types.length} engine node types from server`);
    }).catch(() => {
      // Server unavailable — use built-in palette only
    });
  },

  syncThemeIcon() {
    const theme = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
    const dark = document.querySelector('#themeToggle .theme-icon-dark') as HTMLElement | null;
    const light = document.querySelector('#themeToggle .theme-icon-light') as HTMLElement | null;
    if (dark) dark.style.display = theme === 'dark' ? '' : 'none';
    if (light) light.style.display = theme === 'light' ? '' : 'none';
  },

  renderAll() {
    Canvas.apply();
    Nodes.render();
    Edges.render();
    Inspector.render();
    this.renderTabs();
    this.renderLogs();
    this.renderStatus();
    Minimap.render();
  },

  renderStatus() {
    const dot = document.getElementById('wfStatusDot');
    if (dot) dot.className = 'wf-status-dot ' + (State.isDirty ? 'dirty' : State.lastSaved ? 'saved' : '');
  },

  markDirty() {
    State.isDirty = true;
    this.renderStatus();
  },

  toggleTheme() {
    const cur = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
    const next = cur === 'light' ? 'dark' : 'light';
    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('flow-studio:theme', next); } catch { /* ignore */ }
    this.syncThemeIcon();
  },

  toggleMinimap() {
    const el = document.getElementById('minimap');
    if (!el) return;
    const hidden = el.style.display === 'none';
    el.style.display = hidden ? '' : 'none';
    const btn = document.getElementById('btnMinimap');
    if (btn) btn.classList.toggle('active', hidden);
    if (hidden) Minimap.render();
  },

  togglePanel(panel: 'palette' | 'inspector' | 'logs') {
    const app = document.querySelector('.app');
    if (!app) return;
    const cls = `${panel}-collapsed`;
    app.classList.toggle(cls);
    const collapsed = app.classList.contains(cls);

    // Flip the chevron direction on the toggle button
    if (panel === 'palette') {
      const btn = document.querySelector('.panel-toggle-left') as HTMLElement | null;
      if (btn) btn.innerHTML = collapsed
        ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 18l6-6-6-6"/></svg>'
        : '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 18l-6-6 6-6"/></svg>';
    } else if (panel === 'inspector') {
      const btn = document.querySelector('.panel-toggle-right') as HTMLElement | null;
      if (btn) btn.innerHTML = collapsed
        ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 18l-6-6 6-6"/></svg>'
        : '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M9 18l6-6-6-6"/></svg>';
    } else if (panel === 'logs') {
      const btn = document.querySelector('.panel-toggle-bottom') as HTMLElement | null;
      if (btn) btn.innerHTML = collapsed
        ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 9l6 6 6-6"/></svg>'
        : '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 15l6-6 6 6"/></svg>';
    }
  },

  select(kind: 'node' | 'edge' | null, id?: string) {
    State.selection = kind ? { kind, id: id || null } : { kind: null, id: null };
    Nodes.render();
    Edges.render();
    Inspector.render();
  },

  // ---------- NODE OPS ----------
  createNode(type: string, x: number, y: number, opts: CreateNodeOptions = {}): string {
    const tpl = NodeTypes[type];
    if (!tpl) return '';
    const node: FlowNode = {
      id: uid('n'),
      type,
      name: tpl.label,
      x, y,
      width: 220,
      config: clone(tpl.config || {}),
      status: '',
      parentId: opts.parentId || null,
      parentSlot: opts.parentSlot || null,
    };
    if (tpl.branches) node.branches = [...tpl.branches];
    if (type === 'container') { node.width = 280; node.height = 180; node.collapsed = false; }
    ensureChildren(node);
    State.nodes.set(node.id, node);
    if (node.parentId) {
      const parent = State.nodes.get(node.parentId);
      if (parent) {
        ensureChildren(parent);
        const arr = parent.children![node.parentSlot!]!;
        const idx = (typeof opts.index === 'number') ? Math.max(0, Math.min(opts.index, arr.length)) : arr.length;
        arr.splice(idx, 0, node.id);
      }
    }
    this.markDirty();
    this.renderAll();
    this.select('node', node.id);
    this.log('info', null, `Created node "${node.name}"`);
    return node.id;
  },

  isDescendantOf(maybeDescendantId: string, ancestorId: string): boolean {
    const ancestor = State.nodes.get(ancestorId);
    if (!ancestor || !ancestor.children) return false;
    for (const slot of Object.keys(ancestor.children)) {
      for (const cid of ancestor.children[slot]) {
        if (cid === maybeDescendantId) return true;
        if (this.isDescendantOf(maybeDescendantId, cid)) return true;
      }
    }
    return false;
  },

  reparentNode(nodeId: string, newParentId: string, newSlot: string, index?: number) {
    const node = State.nodes.get(nodeId);
    const newParent = State.nodes.get(newParentId);
    if (!node || !newParent || nodeId === newParentId) return;
    if (this.isDescendantOf(newParentId, nodeId)) return;
    if (node.parentId) {
      const oldParent = State.nodes.get(node.parentId);
      if (oldParent?.children?.[node.parentSlot!]) {
        oldParent.children[node.parentSlot!] = oldParent.children[node.parentSlot!].filter(id => id !== nodeId);
      }
    }
    ensureChildren(newParent);
    const arr = newParent.children![newSlot] || (newParent.children![newSlot] = []);
    if (arr.includes(nodeId)) arr.splice(arr.indexOf(nodeId), 1);
    if (typeof index === 'number') {
      arr.splice(Math.max(0, Math.min(index, arr.length)), 0, nodeId);
    } else {
      arr.push(nodeId);
    }
    node.parentId = newParentId;
    node.parentSlot = newSlot;
    [...State.edges.entries()].forEach(([eid, e]) => {
      if (e.source === newParentId && e.sourcePort === 'branch' &&
          e.sourcePortName === newSlot && e.target === nodeId) {
        State.edges.delete(eid);
      }
    });
    this.markDirty();
    this.renderAll();
    this.select('node', nodeId);
    this.log('info', null, `Moved "${node.name}" into ${newParent.name || newParent.type}/${newSlot}`);
  },

  createChildNode(type: string, parentId: string, slot: string, index?: number): string | null {
    const parent = State.nodes.get(parentId);
    if (!parent) return null;
    const slots = getSlots(parent);
    if (!slots.includes(slot)) return null;
    return this.createNode(type, 0, 0, { parentId, parentSlot: slot, index });
  },

  insertNodeRelative(type: string, refNodeId: string, position: string): string | null {
    const ref = State.nodes.get(refNodeId);
    if (!ref) return null;
    if (ref.parentId) {
      const parent = State.nodes.get(ref.parentId);
      const arr = parent?.children?.[ref.parentSlot!] || [];
      const idx = arr.indexOf(refNodeId);
      const insertAt = position === 'before' ? idx : idx + 1;
      return this.createChildNode(type, ref.parentId, ref.parentSlot!, insertAt);
    }
    const newId = this.createNode(type, ref.x, ref.y + (position === 'before' ? -180 : 180));
    if (position === 'before') {
      const incoming = [...State.edges.values()].find(e => e.target === refNodeId && e.targetPort === 'in');
      if (incoming) {
        incoming.target = newId;
        this.createEdge({ source: newId, sourcePort: 'out', target: refNodeId, targetPort: 'in' });
      }
    } else {
      const sourcePort = position === 'after-error' ? 'error' : 'out';
      const outgoing = [...State.edges.values()].find(e => e.source === refNodeId && e.sourcePort === sourcePort);
      if (outgoing) {
        const oldTarget = outgoing.target;
        outgoing.target = newId;
        this.createEdge({ source: newId, sourcePort: 'out', target: oldTarget, targetPort: 'in' });
      }
    }
    return newId;
  },

  updateNode(id: string, patch: Partial<FlowNode>) {
    const node = State.nodes.get(id);
    if (!node) return;
    Object.assign(node, patch);
    this.markDirty();
    Nodes.render();
    debouncedRender(() => Inspector.render());
    this.triggerValidation();
  },

  updateNodeConfig(id: string, key: string, value: unknown) {
    const node = State.nodes.get(id);
    if (!node) return;
    node.config = { ...(node.config || {}), [key]: value as string | number | boolean };
    this.markDirty();
    Nodes.render();
    this.triggerValidation();
  },

  /** Trigger server-side validation with 5s debounce */
  triggerValidation() {
    debouncedValidation(async () => {
      if (!State.currentWfId) return;
      const wf = State.workflows[State.currentWfId];
      if (!wf) return;
      try {
        // Save first so server has latest state
        await saveWorkflowApi(wf.name, wf);
        const entries = await validateWorkflow(wf.name);
        if (entries.length === 0) {
          this.log('success', null, '✓ Validation passed');
        } else {
          entries.forEach(e => {
            this.log(e.severity === 'error' ? 'error' : 'warn', e.nodeId || null, `Validation: ${e.message}`);
          });
        }
      } catch {
        // Server unavailable — skip validation
      }
    });
  },

  deleteNode(id: string) {
    const node = State.nodes.get(id);
    if (!node) return;
    if (node.parentId) {
      const parent = State.nodes.get(node.parentId);
      if (parent?.children?.[node.parentSlot!]) {
        parent.children[node.parentSlot!] = parent.children[node.parentSlot!].filter(cid => cid !== id);
      }
    }
    const collect = (nid: string, acc: string[] = []): string[] => {
      const n = State.nodes.get(nid);
      if (!n) return acc;
      acc.push(nid);
      if (n.children) {
        Object.values(n.children).forEach(arr => arr.forEach(cid => collect(cid, acc)));
      }
      return acc;
    };
    const toDelete = new Set(collect(id));
    toDelete.forEach(nid => State.nodes.delete(nid));
    [...State.edges.entries()].forEach(([eid, e]) => {
      if (toDelete.has(e.source) || toDelete.has(e.target)) State.edges.delete(eid);
    });
    this.markDirty();
    this.select(null);
    this.renderAll();
  },

  toggleCollapse(id: string) {
    const node = State.nodes.get(id);
    if (!node) return;
    node.collapsed = !node.collapsed;
    this.markDirty();
    this.renderAll();
  },

  addBranch(nodeId: string) {
    const node = State.nodes.get(nodeId);
    if (!node) return;
    node.branches = node.branches || [];
    node.branches.push(`case_${node.branches.length + 1}`);
    this.markDirty();
    this.renderAll();
  },

  removeBranch(nodeId: string, index: number) {
    const node = State.nodes.get(nodeId);
    if (!node || !node.branches) return;
    const removed = node.branches.splice(index, 1)[0];
    [...State.edges.entries()].forEach(([eid, e]) => {
      if (e.source === nodeId && e.sourcePortName === removed) State.edges.delete(eid);
    });
    this.markDirty();
    this.renderAll();
  },

  renameBranch(nodeId: string, index: number, newName: string) {
    const node = State.nodes.get(nodeId);
    if (!node || !node.branches) return;
    const old = node.branches[index];
    node.branches[index] = newName;
    State.edges.forEach(e => {
      if (e.source === nodeId && e.sourcePortName === old) e.sourcePortName = newName;
    });
    this.markDirty();
    Nodes.render();
    Edges.render();
  },

  // ---------- EDGE OPS ----------
  createEdge({ source, sourcePort, sourcePortName, target, targetPort }: CreateEdgeParams) {
    for (const e of State.edges.values()) {
      if (e.source === source && e.sourcePort === sourcePort &&
          e.sourcePortName === sourcePortName && e.target === target) return;
    }
    const id = uid('e');
    State.edges.set(id, { id, source, sourcePort, sourcePortName, target, targetPort });
    this.markDirty();
    Edges.render();
    const s = State.nodes.get(source)?.name || '?';
    const t = State.nodes.get(target)?.name || '?';
    this.log('info', null, `Connected ${s} → ${t}`);
  },

  deleteEdge(id: string) {
    State.edges.delete(id);
    this.markDirty();
    this.select(null);
    Edges.render();
    Inspector.render();
  },

  // ---------- WORKFLOW OPS ----------
  nextUntitledName(): string {
    const existing = new Set(Object.values(State.workflows).map(w => w.name));
    if (!existing.has('Untitled')) return 'Untitled';
    for (let i = 2; ; i++) {
      const candidate = `Untitled ${i}`;
      if (!existing.has(candidate)) return candidate;
    }
  },

  newWorkflow(silent = false) {
    const id = uid('wf');
    const name = this.nextUntitledName();
    const wf: WorkflowData = {
      id, name,
      nodes: [], edges: [],
      createdAt: now(), updatedAt: now()
    };
    State.workflows[id] = wf;
    State.currentWfId = id;
    if (!State.openTabs.includes(id)) State.openTabs.push(id);
    State.nodes.clear();
    State.edges.clear();
    State.isDirty = false;
    State.logs = [];
    // Set the title input BEFORE createNode/saveToStorage so the name isn't overwritten
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    if (titleInput) titleInput.value = wf.name;
    this.createNode('start', 240, 180);
    State.isDirty = false;
    this.saveToStorage();
    this.renderAll();
    if (!silent) toast('New workflow created', 'success');
  },

  async saveWorkflow() {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) return;
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    wf.name = titleInput?.value || wf.name || 'Untitled';
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
    wf.updatedAt = now();
    State.isDirty = false;
    State.lastSaved = now();
    this.saveToStorage();

    // Always try server save
    try {
      await saveWorkflowApi(wf.name, wf);
      toast('Saved to server', 'success');
    } catch {
      toast('Saved locally', 'success');
    }

    this.renderAll();
  },

  loadWorkflow(id: string) {
    const wf = State.workflows[id];
    if (!wf) return;
    // If switching tabs, save current state first
    if (State.currentWfId && State.currentWfId !== id) {
      this.snapshotCurrentWorkflow();
    }
    State.currentWfId = id;
    if (!State.openTabs.includes(id)) State.openTabs.push(id);
    State.nodes = new Map((wf.nodes || []).map(n => [n.id, n]));
    State.edges = new Map((wf.edges || []).map(e => [e.id, e]));
    State.isDirty = false;
    State.selection = { kind: null, id: null };
    State.logs = [];
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    if (titleInput) titleInput.value = wf.name;
    this.renderAll();
    Canvas.fitToView();
  },

  /** Save current nodes/edges back into the workflow object without persisting */
  snapshotCurrentWorkflow() {
    if (!State.currentWfId) return;
    const wf = State.workflows[State.currentWfId];
    if (!wf) return;
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    wf.name = titleInput?.value || wf.name;
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
  },

  deleteWorkflow() {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) return;
    this.showModal({
      title: 'Delete workflow?',
      body: `"${wf.name}" will be permanently removed.`,
      confirmLabel: 'Delete',
      danger: true,
      onConfirm: async () => {
        try { await deleteWorkflowApi(wf.name); } catch { /* server unavailable */ }
        const delId = State.currentWfId || '';
        delete State.workflows[delId];
        State.openTabs = State.openTabs.filter(t => t !== delId);
        State.currentWfId = null;
        if (State.openTabs.length) {
          this.loadWorkflow(State.openTabs[0]);
        } else {
          this.newWorkflow(true);
        }
        this.saveToStorage();
        toast('Workflow deleted', 'info');
      }
    });
  },

  exportWorkflow() {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) return;
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    wf.name = titleInput?.value || wf.name || 'Untitled';
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
    const data = JSON.stringify(wf, null, 2);
    const blob = new Blob([data], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${wf.name.replace(/[^a-z0-9]+/gi, '_')}.flow.json`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast('Exported JSON', 'success');
  },

  importWorkflow() {
    document.getElementById('fileInput')?.click();
  },

  handleImport(file: File) {
    const reader = new FileReader();
    reader.onload = (e) => {
      try {
        const data = JSON.parse(e.target?.result as string);
        if (!data.id || !Array.isArray(data.nodes)) throw new Error('Invalid workflow JSON');
        if (State.workflows[data.id]) data.id = uid('wf');
        data.updatedAt = now();
        State.workflows[data.id] = data;
        this.saveToStorage();
        this.loadWorkflow(data.id);
        toast('Imported workflow', 'success');
      } catch (err) {
        toast('Import failed: ' + (err as Error).message, 'error');
      }
    };
    reader.readAsText(file);
  },

  async openFromServer() {
    // Fetch workflow list from server
    let workflows: { id: string; name?: string; updatedAt?: string }[];
    try {
      const raw = await listWorkflows() as { id: string; name?: string; updatedAt?: string }[];
      workflows = raw;
    } catch (err) {
      this.showModal({
        title: 'Cannot connect',
        body: 'Failed to reach the workflow server. Make sure it is running.',
        confirmLabel: 'OK',
        onConfirm: () => {},
      });
      return;
    }

    // Build modal dialog — show even if empty (with a message)
    const backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    const modal = document.createElement('div');
    modal.className = 'modal open-dialog';

    const listHtml = workflows.length
      ? workflows.map(w => `
          <div class="open-dialog-item" data-wf-id="${escapeHtml(w.id)}">
            <div class="open-dialog-item-name">${escapeHtml(w.name || w.id)}</div>
            <div class="open-dialog-item-meta mono">${escapeHtml(w.id)}</div>
          </div>
        `).join('')
      : `<div style="padding: 24px; text-align: center; color: var(--text-3);">
           <div style="font-size: 13px; margin-bottom: 4px;">No workflows on server</div>
           <div style="font-size: 11px;">Save a workflow first, then open it here.</div>
         </div>`;

    modal.innerHTML = `
      <h3>Open Workflow</h3>
      ${workflows.length ? `<div class="open-dialog-search">
        <input placeholder="Filter…" class="open-dialog-filter" />
      </div>` : ''}
      <div class="open-dialog-list">
        ${listHtml}
      </div>
      <div class="modal-actions">
        <button class="modal-btn-secondary">Cancel</button>
      </div>
    `;
    backdrop.appendChild(modal);
    document.body.appendChild(backdrop);

    // Filter input
    const filterInput = modal.querySelector('.open-dialog-filter') as HTMLInputElement;
    filterInput.addEventListener('input', () => {
      const q = filterInput.value.toLowerCase();
      modal.querySelectorAll('.open-dialog-item').forEach(item => {
        const el = item as HTMLElement;
        const name = el.querySelector('.open-dialog-item-name')?.textContent?.toLowerCase() || '';
        const id = el.querySelector('.open-dialog-item-meta')?.textContent?.toLowerCase() || '';
        el.style.display = (!q || name.includes(q) || id.includes(q)) ? '' : 'none';
      });
    });
    filterInput.focus();

    // Click to open a workflow
    modal.querySelectorAll('.open-dialog-item').forEach(item => {
      item.addEventListener('click', async () => {
        const wfId = (item as HTMLElement).dataset.wfId!;
        backdrop.remove();
        try {
          const record = await getWorkflow(wfId) as { yaml?: string; sidecarJson?: string };
          if (!record?.yaml) { toast('Empty workflow', 'error'); return; }
          const data = JSON.parse(record.yaml);
          // Merge sidecar metadata
          if (record.sidecarJson) {
            try {
              const sidecar = JSON.parse(record.sidecarJson);
              if (sidecar.createdAt) data.createdAt = sidecar.createdAt;
              if (sidecar.updatedAt) data.updatedAt = sidecar.updatedAt;
            } catch { /* ignore */ }
          }
          if (!data.id) data.id = uid('wf');
          if (!data.name) data.name = wfId;
          if (!data.nodes) data.nodes = [];
          if (!data.edges) data.edges = [];
          data.updatedAt = data.updatedAt || now();
          data.createdAt = data.createdAt || now();
          // Use a local UID for state tracking, preserve server name
          const localId = uid('wf');
          data.id = localId;
          State.workflows[localId] = data;
          this.saveToStorage();
          this.loadWorkflow(localId);
          toast(`Opened: ${data.name}`, 'success');
        } catch (err) {
          toast('Failed to open: ' + (err as Error).message, 'error');
        }
      });
    });

    // Cancel
    modal.querySelector('.modal-btn-secondary')!.addEventListener('click', () => backdrop.remove());
    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) backdrop.remove(); });
  },

  renderTabs() {
    const el = document.getElementById('tabBarScroll');
    if (!el) return;
    // Clean up stale tabs
    State.openTabs = State.openTabs.filter(id => State.workflows[id]);
    if (State.currentWfId && !State.openTabs.includes(State.currentWfId)) {
      State.openTabs.push(State.currentWfId);
    }

    el.innerHTML = State.openTabs.map(id => {
      const wf = State.workflows[id];
      if (!wf) return '';
      const active = id === State.currentWfId;
      const dirty = active && State.isDirty;
      return `
        <div class="tab ${active ? 'active' : ''} ${dirty ? 'dirty' : ''}" data-tab-id="${id}">
          <span class="tab-name">${escapeHtml(wf.name)}</span>
          <span class="tab-dirty"></span>
          <button class="tab-close" data-action="close-tab" data-tab-id="${id}" title="Close">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
          </button>
        </div>`;
    }).join('');

    // Click to switch tab
    el.querySelectorAll('.tab').forEach(tab => {
      tab.addEventListener('click', (e) => {
        if ((e.target as HTMLElement).closest('.tab-close')) return;
        this.loadWorkflow((tab as HTMLElement).dataset.tabId!);
      });
    });
    // Close tab
    el.querySelectorAll('[data-action="close-tab"]').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        this.closeTab((btn as HTMLElement).dataset.tabId!);
      });
    });
  },

  closeTab(id: string) {
    const idx = State.openTabs.indexOf(id);
    if (idx < 0) return;
    // Snapshot current before closing
    if (State.currentWfId === id) {
      this.snapshotCurrentWorkflow();
    }
    State.openTabs.splice(idx, 1);
    // If closing the active tab, switch to an adjacent tab or create new
    if (State.currentWfId === id) {
      if (State.openTabs.length > 0) {
        const nextIdx = Math.min(idx, State.openTabs.length - 1);
        this.loadWorkflow(State.openTabs[nextIdx]);
      } else {
        this.newWorkflow(true);
      }
    } else {
      this.renderTabs();
    }
  },

  deleteWorkflowById(id: string) {
    const wf = State.workflows[id];
    if (!wf) return;
    if (!confirm(`Delete "${wf.name}"?`)) return;
    delete State.workflows[id];
    State.openTabs = State.openTabs.filter(t => t !== id);
    if (State.currentWfId === id) {
      if (State.openTabs.length) this.loadWorkflow(State.openTabs[0]);
      else this.newWorkflow(true);
    }
    this.saveToStorage();
    this.renderTabs();
    toast('Workflow deleted', 'info');
  },

  // ---------- STORAGE ----------
  clearStorage() {
    State.workflows = {};
    State.currentWfId = null;
    try {
      localStorage.removeItem('flow-studio:workflows');
      localStorage.removeItem('flow-studio:currentId');
    } catch { /* ignore */ }
  },

  saveToStorage() {
    if (State.currentWfId) {
      const wf = State.workflows[State.currentWfId];
      if (wf) {
        const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
        wf.name = titleInput?.value || wf.name;
        wf.nodes = [...State.nodes.values()];
        wf.edges = [...State.edges.values()];
      }
    }
    try {
      localStorage.setItem('flow-studio:workflows', JSON.stringify(State.workflows));
      localStorage.setItem('flow-studio:currentId', State.currentWfId || '');
      localStorage.setItem('flow-studio:openTabs', JSON.stringify(State.openTabs));
    } catch {
      // Storage unavailable
    }
  },

  loadWorkflowsFromStorage() {
    try {
      const raw = localStorage.getItem('flow-studio:workflows');
      if (raw) {
        const parsed = JSON.parse(raw);
        // Deduplicate by name — drop extra copies of the same name (migration fix)
        const seen = new Set<string>();
        const clean: Record<string, WorkflowData> = {};
        for (const wf of Object.values(parsed) as WorkflowData[]) {
          if (!seen.has(wf.name)) {
            seen.add(wf.name);
            clean[wf.id] = wf;
          }
        }
        State.workflows = clean;
      }
      const currentId = localStorage.getItem('flow-studio:currentId');
      // Restore open tabs
      try {
        const tabsRaw = localStorage.getItem('flow-studio:openTabs');
        if (tabsRaw) {
          State.openTabs = JSON.parse(tabsRaw).filter((id: string) => State.workflows[id]);
        }
      } catch { /* ignore */ }
      if (currentId && State.workflows[currentId]) {
        this.loadWorkflow(currentId);
      } else {
        const first = Object.values(State.workflows)[0];
        if (first) this.loadWorkflow(first.id);
      }
    } catch {
      // Nothing loaded
    }
  },

  // ---------- LOGS ----------
  log(level: string, nodeId: string | null, msg: string) {
    const entry = {
      t: new Date().toLocaleTimeString('en-US', { hour12: false }),
      level, nodeId, msg
    };
    State.logs.push(entry);
    if (State.logs.length > 500) State.logs.shift();
    this.renderLogs();
  },

  clearLogs() {
    State.logs = [];
    this.renderLogs();
  },

  renderLogs() {
    const body = document.getElementById('logsBody');
    const count = document.getElementById('logCount');
    if (!body || !count) return;
    count.textContent = String(State.logs.length);
    if (!State.logs.length) {
      body.innerHTML = `<div class="log-empty">No execution logs yet. Hit Run to start.</div>`;
      return;
    }
    body.innerHTML = State.logs.slice().reverse().map(e => {
      const nodeName = e.nodeId ? (State.nodes.get(e.nodeId)?.name || e.nodeId) : 'system';
      return `<div class="log-line">
        <span class="log-time">${escapeHtml(e.t)}</span>
        <span class="log-level ${e.level}">${escapeHtml(e.level)}</span>
        <span class="log-node">[${escapeHtml(nodeName)}]</span>
        <span class="log-msg">${escapeHtml(e.msg)}</span>
      </div>`;
    }).join('');
  },

  // ---------- CLIPBOARD ----------
  _clipboard: null as { nodes: FlowNode[]; edges: FlowEdge[] } | null,

  copySelected() {
    if (State.selection.kind !== 'node' || !State.selection.id) return;
    const rootId = State.selection.id;

    // Collect the selected node and all its descendants
    const collectIds = (nid: string, acc: Set<string>): Set<string> => {
      acc.add(nid);
      const n = State.nodes.get(nid);
      if (n?.children) {
        Object.values(n.children).forEach(arr => arr.forEach(cid => collectIds(cid, acc)));
      }
      return acc;
    };
    const ids = collectIds(rootId, new Set<string>());

    // Deep-clone nodes
    const nodes = [...ids].map(id => clone(State.nodes.get(id)!));

    // Collect edges that are fully within the copied set
    const edges: FlowEdge[] = [];
    State.edges.forEach(e => {
      if (ids.has(e.source) && ids.has(e.target)) {
        edges.push(clone(e));
      }
    });

    this._clipboard = { nodes, edges };
    toast(`Copied ${nodes.length} node${nodes.length > 1 ? 's' : ''}`, 'info');
  },

  pasteClipboard() {
    if (!this._clipboard) return;
    const { nodes, edges } = this._clipboard;

    // Build old→new ID mapping
    const idMap = new Map<string, string>();
    nodes.forEach(n => idMap.set(n.id, uid('n')));
    edges.forEach(e => idMap.set(e.id, uid('e')));

    // Paste offset so pasted nodes don't overlap originals
    const offset = 40;

    // Create new nodes with remapped IDs
    nodes.forEach(n => {
      const newNode: FlowNode = {
        ...clone(n),
        id: idMap.get(n.id)!,
        x: n.x + offset,
        y: n.y + offset,
        status: '',
        parentId: n.parentId ? (idMap.get(n.parentId) || null) : null,
        parentSlot: n.parentSlot,
      };
      // Remap children IDs
      if (newNode.children) {
        const newChildren: Record<string, string[]> = {};
        for (const [slot, arr] of Object.entries(newNode.children)) {
          newChildren[slot] = arr.map(cid => idMap.get(cid) || cid);
        }
        newNode.children = newChildren;
      }
      State.nodes.set(newNode.id, newNode);
    });

    // Create new edges with remapped IDs
    edges.forEach(e => {
      const newEdge: FlowEdge = {
        ...clone(e),
        id: idMap.get(e.id)!,
        source: idMap.get(e.source) || e.source,
        target: idMap.get(e.target) || e.target,
      };
      State.edges.set(newEdge.id, newEdge);
    });

    this.markDirty();
    // Select the root pasted node
    const newRootId = idMap.get(nodes[0].id);
    if (newRootId) this.select('node', newRootId);
    this.renderAll();
    toast(`Pasted ${nodes.length} node${nodes.length > 1 ? 's' : ''}`, 'success');
  },

  // ---------- SHORTCUTS ----------
  bindShortcuts() {
    window.addEventListener('keydown', (e) => {
      if ((e.target as HTMLElement).tagName === 'INPUT' || (e.target as HTMLElement).tagName === 'TEXTAREA') return;
      if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); this.saveWorkflow(); }
      if ((e.ctrlKey || e.metaKey) && e.key === 'n') { e.preventDefault(); this.newWorkflow(); }
      if ((e.ctrlKey || e.metaKey) && e.key === 'c') { this.copySelected(); }
      if ((e.ctrlKey || e.metaKey) && e.key === 'v') { this.pasteClipboard(); }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (State.selection.kind === 'node') this.deleteNode(State.selection.id!);
        if (State.selection.kind === 'edge') this.deleteEdge(State.selection.id!);
      }
      if (e.key === 'Escape') this.select(null);
      if (e.key === '=' || e.key === '+') Canvas.zoom(1.15);
      if (e.key === '-') Canvas.zoom(0.87);
      if (e.key === '0') Canvas.reset();
      if (e.key === 'f') Canvas.fitToView();
    });
  },

  bindTitleInput() {
    document.getElementById('wfTitleInput')?.addEventListener('input', () => this.markDirty());
  },

  bindFileInput() {
    document.getElementById('fileInput')?.addEventListener('change', (e) => {
      const input = e.target as HTMLInputElement;
      if (input.files?.[0]) this.handleImport(input.files[0]);
      input.value = '';
    });
  },

  bindTopbarActions() {
    // Event delegation for all data-action buttons
    document.addEventListener('click', (e) => {
      const btn = (e.target as HTMLElement).closest('[data-action]') as HTMLElement | null;
      if (!btn) return;
      const action = btn.dataset.action;
      switch (action) {
        case 'new-workflow': this.newWorkflow(); break;
        case 'open-workflow': this.openFromServer(); break;
        case 'save-workflow': this.saveWorkflow(); break;
        case 'import-workflow': this.importWorkflow(); break;
        case 'export-workflow': this.exportWorkflow(); break;
        case 'delete-workflow': this.deleteWorkflow(); break;
        case 'toggle-theme': this.toggleTheme(); break;
        case 'exec-run': Exec.run(); break;
        case 'exec-pause': Exec.pause(); break;
        case 'exec-resume': Exec.resume(); break;
        case 'exec-rerun': Exec.rerun(); break;
        case 'exec-cancel': Exec.cancel(); break;
        case 'zoom-in': Canvas.zoom(1.15); break;
        case 'zoom-out': Canvas.zoom(0.87); break;
        case 'fit-view': Canvas.fitToView(); break;
        case 'reset-view': Canvas.reset(); break;
        case 'toggle-minimap': this.toggleMinimap(); break;
        case 'toggle-palette': this.togglePanel('palette'); break;
        case 'toggle-inspector': this.togglePanel('inspector'); break;
        case 'toggle-logs': this.togglePanel('logs'); break;
        case 'clear-logs': this.clearLogs(); break;
      }
    });

    // Palette search
    document.getElementById('paletteSearchInput')?.addEventListener('input', (e) => {
      Palette.filter((e.target as HTMLInputElement).value);
    });
  },

  // ---------- MODAL ----------
  showModal({ title, body, confirmLabel = 'Confirm', danger = false, onConfirm }: ModalOptions) {
    const backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    const modal = document.createElement('div');
    modal.className = 'modal';
    modal.innerHTML = `
      <h3>${escapeHtml(title)}</h3>
      <p>${escapeHtml(body)}</p>
      <div class="modal-actions">
        <button class="modal-btn-secondary">Cancel</button>
        <button class="${danger ? 'modal-btn-danger' : 'modal-btn-primary'}">
          ${escapeHtml(confirmLabel)}
        </button>
      </div>
    `;
    backdrop.appendChild(modal);
    document.body.appendChild(backdrop);

    const cancelBtn = modal.querySelector('.modal-btn-secondary')!;
    const confirmBtn = modal.querySelector(danger ? '.modal-btn-danger' : '.modal-btn-primary')!;
    cancelBtn.addEventListener('click', () => backdrop.remove());
    confirmBtn.addEventListener('click', () => { backdrop.remove(); onConfirm(); });
    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) backdrop.remove(); });
  },
};
