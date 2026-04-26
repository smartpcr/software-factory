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
import { Inspector, ensureServerWorkflows } from './inspector';
import { disposeAllEditors } from './csharp-editor';
import { Exec } from './exec';
import { saveWorkflow as saveWorkflowApi, deleteWorkflowApi, listWorkflows, getWorkflow, fetchNodeTypes, validateWorkflow, validateNodesBatch, listRuns } from './api';
import { loadServerNodeTypes, loadFallbackNodeTypes, normalizeWorkflowNodeTypes, resolveNodeType } from './registry';

function buildCallbacks(): AppCallbacks {
  return {
    select: (kind, id) => App.select(kind, id),
    createNode: (type, x, y, opts) => App.createNode(type, x, y, opts),
    createChildNode: (type, parentId, slot, index) => App.createChildNode(type, parentId, slot, index),
    insertNodeRelative: (type, refNodeId, position) => App.insertNodeRelative(type, refNodeId, position),
    reparentNode: (nodeId, newParentId, newSlot, index) => App.reparentNode(nodeId, newParentId, newSlot, index),
    unparentNode: (nodeId, canvasX, canvasY) => App.unparentNode(nodeId, canvasX, canvasY),
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
    Exec.onRunFinished(() => this.loadRunHistory());
    loadFallbackNodeTypes(); // Minimal palette until server responds
    Palette.onCreateNode((type, x, y) => this.createNode(type, x, y));
    Palette.render();
    this.bindShortcuts();
    this.bindTitleInput();
    this.bindFileInput();
    this.bindTopbarActions();
    this.syncThemeIcon();
    this.renderAll();

    // Pre-fetch server workflow list for subflow dropdowns
    ensureServerWorkflows();

    // Load node types from server (single source of truth)
    fetchNodeTypes().then(types => {
      loadServerNodeTypes(types);
      // Normalize all stored workflows
      for (const wf of Object.values(State.workflows)) {
        normalizeWorkflowNodeTypes(wf.nodes || []);
      }
      // Fix current in-memory nodes
      State.nodes.forEach(n => {
        const resolved = resolveNodeType(n.runtimeType);
        if (resolved !== n.runtimeType) n.runtimeType = resolved;
      });
      this.saveToStorage();
      Palette.render();
      this.renderAll();
      // Seed demo on first-ever use (now that all types are available)
      if (Object.keys(State.workflows).length === 0) {
        this.newWorkflow(true);
      }
      this.log('info', null, `Loaded ${types.length} node types from server`);
    }).catch(() => {
      // Server unavailable — use minimal fallback
      loadFallbackNodeTypes();
      Palette.render();
      // Create a basic workflow with fallback types
      if (Object.keys(State.workflows).length === 0) {
        this.newWorkflow(true);
      }
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
    if (State.currentWfId) State.dirtyWorkflows.add(State.currentWfId);
    this.renderStatus();
    this.renderTabs();
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

  toggleLock() {
    State.isLocked = !State.isLocked;
    const btn = document.getElementById('btnLock');
    if (btn) {
      btn.classList.toggle('active', State.isLocked);
      const unlocked = btn.querySelector('.icon-unlocked') as HTMLElement;
      const locked = btn.querySelector('.icon-locked') as HTMLElement;
      if (unlocked) unlocked.style.display = State.isLocked ? 'none' : '';
      if (locked) locked.style.display = State.isLocked ? '' : 'none';
    }
    const app = document.querySelector('.app');
    if (app) app.classList.toggle('canvas-locked', State.isLocked);
    toast(State.isLocked ? 'Canvas locked' : 'Canvas unlocked', 'info');
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
    if (State.isLocked) { toast('Canvas is locked', 'warn'); return ''; }
    // Resolve to canonical type (handles case mismatches)
    const resolved = NodeTypes[type] ? type : Object.keys(NodeTypes).find(k => k.toLowerCase() === type.toLowerCase()) || type;
    const tpl = NodeTypes[resolved];
    if (!tpl) return '';
    const node: FlowNode = {
      nodeId: uid('n'),
      runtimeType: resolved,
      nodeName: tpl.label,
      x, y,
      width: 220,
      config: clone(tpl.config || {}),
      status: '',
      parentId: opts.parentId || null,
      parentSlot: opts.parentSlot || null,
    };
    if (tpl.branches) node.branches = [...tpl.branches];
    if (resolved.toLowerCase() === 'group') { node.width = 280; node.collapsed = false; }
    ensureChildren(node);
    State.nodes.set(node.nodeId, node);
    if (node.parentId) {
      const parent = State.nodes.get(node.parentId);
      if (parent) {
        ensureChildren(parent);
        const arr = parent.children![node.parentSlot!]!;
        const idx = (typeof opts.index === 'number') ? Math.max(0, Math.min(opts.index, arr.length)) : arr.length;
        arr.splice(idx, 0, node.nodeId);
      }
    }
    this.markDirty();
    this.renderAll();
    this.select('node', node.nodeId);
    this.log('info', null, `Created node "${node.nodeName}"`);
    return node.nodeId;
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

    // Remove old parent-child relationship and cleanup old container edges
    if (node.parentId) {
      const oldParent = State.nodes.get(node.parentId);
      if (oldParent?.children?.[node.parentSlot!]) {
        oldParent.children[node.parentSlot!] = oldParent.children[node.parentSlot!].filter(id => id !== nodeId);
      }
      // Remove auto-created container edges involving this node
      this.removeContainerEdges(nodeId, node.parentId, node.parentSlot!);
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

    // Auto-create container edges for the entire slot
    this.rebuildContainerEdges(newParentId, newSlot);

    this.markDirty();
    this.renderAll();
    this.select('node', nodeId);
    this.log('info', null, `Moved "${node.nodeName}" into ${newParent.nodeName || newParent.runtimeType}/${newSlot}`);
  },

  /** Remove a node from its parent container, making it a top-level canvas node */
  unparentNode(nodeId: string, canvasX: number, canvasY: number) {
    const node = State.nodes.get(nodeId);
    if (!node || !node.parentId) return;
    const oldParent = State.nodes.get(node.parentId);
    if (oldParent?.children?.[node.parentSlot!]) {
      oldParent.children[node.parentSlot!] = oldParent.children[node.parentSlot!].filter(id => id !== nodeId);
    }
    this.removeContainerEdges(nodeId, node.parentId, node.parentSlot!);
    this.rebuildContainerEdges(node.parentId, node.parentSlot!);
    node.parentId = null;
    node.parentSlot = null;
    node.x = canvasX;
    node.y = canvasY;
    this.markDirty();
    this.renderAll();
    this.select('node', nodeId);
    this.log('info', null, `Moved "${node.nodeName}" to canvas`);
  },

  createChildNode(type: string, parentId: string, slot: string, index?: number): string | null {
    const parent = State.nodes.get(parentId);
    if (!parent) return null;
    const slots = getSlots(parent);
    if (!slots.includes(slot)) return null;
    const newId = this.createNode(type, 0, 0, { parentId, parentSlot: slot, index });
    if (newId) {
      this.rebuildContainerEdges(parentId, slot);
    }
    return newId;
  },

  /** Determine the triggerMessageType for a container slot */
  containerSlotMessageType(parentType: string, _slot: string): string {
    const loopTypes = ['While', 'ForEach', 'while', 'foreach'];
    return loopTypes.includes(parentType) ? 'Next' : 'Complete';
  },

  /** Remove all auto-created container edges for a node in a slot */
  removeContainerEdges(nodeId: string, parentId: string, slot: string) {
    const parent = State.nodes.get(parentId);
    const siblings = parent?.children?.[slot] || [];
    // Remove edges: parent→node, node→sibling, sibling→node
    [...State.edges.entries()].forEach(([eid, e]) => {
      // Parent→first child
      if (e.sourceNodeId === parentId && e.targetNodeId === nodeId && (e.sourcePort === slot || e.sourcePortName === slot)) {
        State.edges.delete(eid);
      }
      // Sibling chain involving this node
      if ((e.sourceNodeId === nodeId && siblings.includes(e.targetNodeId)) ||
          (e.targetNodeId === nodeId && siblings.includes(e.sourceNodeId))) {
        State.edges.delete(eid);
      }
    });
  },

  /** Rebuild all container edges for a specific slot: parent→first, chain siblings */
  rebuildContainerEdges(parentId: string, slot: string) {
    const parent = State.nodes.get(parentId);
    if (!parent?.children?.[slot]) return;
    const children = parent.children[slot];
    if (children.length === 0) return;

    const msgType = this.containerSlotMessageType(parent.runtimeType, slot);

    // Remove existing auto-created edges for this slot
    [...State.edges.entries()].forEach(([eid, e]) => {
      // Parent→child in this slot
      if (e.sourceNodeId === parentId && (e.sourcePort === slot || e.sourcePortName === slot) &&
          children.includes(e.targetNodeId)) {
        State.edges.delete(eid);
      }
      // Sibling chains within this slot
      if (children.includes(e.sourceNodeId) && children.includes(e.targetNodeId) &&
          e.sourcePort === 'out' && e.targetPort === 'in') {
        State.edges.delete(eid);
      }
    });

    // Parent → first child
    this._insertEdge(parentId, slot, children[0], 'in', msgType, slot);

    // Chain siblings: child[0]→child[1]→child[2]→...
    for (let i = 0; i < children.length - 1; i++) {
      this._insertEdge(children[i], 'out', children[i + 1], 'in');
    }

    // For loop containers (While/ForEach): add feedback edge from last child back to parent
    const loopTypes = ['While', 'ForEach', 'while', 'foreach'];
    if (loopTypes.includes(parent.runtimeType)) {
      const lastChild = children[children.length - 1];
      // Remove any existing feedback edge
      [...State.edges.entries()].forEach(([eid, e]) => {
        if (e.sourceNodeId === lastChild && e.targetNodeId === parentId &&
            e.sourcePort === 'out' && e.targetPort === 'in') {
          State.edges.delete(eid);
        }
      });
      this._insertEdge(lastChild, 'out', parentId, 'in');
    }
  },

  /** Ensure all container nodes have proper edges (called on workflow load) */
  ensureAllContainerEdges() {
    for (const node of State.nodes.values()) {
      if (!node.children) continue;
      for (const slot of Object.keys(node.children)) {
        if (node.children[slot].length > 0) {
          this.rebuildContainerEdges(node.nodeId, slot);
        }
      }
    }
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
      const incoming = [...State.edges.values()].find(e => e.targetNodeId === refNodeId && e.targetPort === 'in');
      if (incoming) {
        incoming.targetNodeId = newId;
        this.createEdge({ sourceNodeId: newId, sourcePort: 'out', targetNodeId: refNodeId, targetPort: 'in' });
      }
    } else {
      const sourcePort = position === 'after-error' ? 'error' : 'out';
      const outgoing = [...State.edges.values()].find(e => e.sourceNodeId === refNodeId && e.sourcePort === sourcePort);
      if (outgoing) {
        const oldTarget = outgoing.targetNodeId;
        outgoing.targetNodeId = newId;
        this.createEdge({ sourceNodeId: newId, sourcePort: 'out', targetNodeId: oldTarget, targetPort: 'in' });
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

  /** Trigger node validation — immediate client-side + debounced server-side */
  triggerValidation() {
    // Immediate client-side validation
    State.nodes.forEach(n => {
      if (n.runtimeType.toLowerCase() === 'group') {
        Inspector.validateGroupNode(n);
      }
    });
    Nodes.render();

    debouncedValidation(async () => {
      if (!State.currentWfId) return;
      try {
        // Build node definitions for validation
        const allNodes = [...State.nodes.values()];
        const nodeDefs = allNodes.map(n => {
          const { childNodes: _drop, childConnections: _drop2, ...configWithout } = (n.config || {}) as Record<string, unknown>;
          const cleanConfig = { ...configWithout };
          const def: Record<string, unknown> = {
            nodeId: n.nodeId,
            runtimeType: n.runtimeType,
            nodeName: n.nodeName,
            configuration: cleanConfig,
            ...configWithout,
          };
          // Include childNodes for Group nodes so server-side validation works
          if (n.runtimeType.toLowerCase() === 'group') {
            let children = allNodes.filter(c => c.parentId === n.nodeId);
            // Also check children from config (may not have parentId set in older sessions)
            if (children.length === 0 && n.children) {
              const childIds = Object.values(n.children).flat();
              children = childIds.map(id => allNodes.find(c => c.nodeId === id)).filter(Boolean) as typeof allNodes;
            }
            def.childNodes = children.map(c => ({ nodeId: c.nodeId, runtimeType: c.runtimeType, nodeName: c.nodeName }));
          }
          return def;
        });

        const entries = await validateNodesBatch(nodeDefs);

        // Clear all existing validation errors
        State.nodes.forEach(n => { n.validationErrors = undefined; });

        // Group errors by nodeId
        const errorsByNode = new Map<string, string[]>();
        for (const entry of entries) {
          if (!entry.nodeId) continue;
          if (!errorsByNode.has(entry.nodeId)) errorsByNode.set(entry.nodeId, []);
          errorsByNode.get(entry.nodeId)!.push(entry.message);
        }

        // Apply to nodes
        for (const [nodeId, errors] of errorsByNode) {
          const node = State.nodes.get(nodeId);
          if (node) node.validationErrors = errors;
        }

        Nodes.render();

        if (entries.length === 0) {
          this.log('success', null, '✓ All nodes valid');
        } else {
          this.log('warn', null, `⚠ ${entries.length} validation issue${entries.length > 1 ? 's' : ''}`);
        }
      } catch {
        // Server unavailable — skip validation
      }
    });
  },

  deleteNode(id: string) {
    if (State.isLocked) { toast('Canvas is locked', 'warn'); return; }
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
      if (toDelete.has(e.sourceNodeId) || toDelete.has(e.targetNodeId)) State.edges.delete(eid);
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
      if (e.sourceNodeId === nodeId && e.sourcePortName === removed) State.edges.delete(eid);
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
      if (e.sourceNodeId === nodeId && e.sourcePortName === old) e.sourcePortName = newName;
    });
    this.markDirty();
    Nodes.render();
    Edges.render();
  },

  // ---------- EDGE OPS ----------

  /** Insert an edge silently (no lock check, no dirty flag, no log). Used by container auto-wiring. */
  _insertEdge(sourceNodeId: string, sourcePort: string, targetNodeId: string, targetPort: string, triggerMessageType = 'Complete', sourcePortName?: string) {
    // Check for duplicate
    for (const e of State.edges.values()) {
      if (e.sourceNodeId === sourceNodeId && e.targetNodeId === targetNodeId &&
          (e.sourcePort === sourcePort || e.sourcePortName === sourcePortName) &&
          e.targetPort === targetPort) {
        e._internal = true; // Mark existing as internal
        return;
      }
    }
    const id = uid('e');
    State.edges.set(id, { id, sourceNodeId, sourcePort, sourcePortName, targetNodeId, targetPort, triggerMessageType, _internal: true });
  },

  createEdge({ sourceNodeId, sourcePort, sourcePortName, targetNodeId, targetPort }: CreateEdgeParams) {
    if (State.isLocked) return;
    for (const e of State.edges.values()) {
      if (e.sourceNodeId === sourceNodeId && e.sourcePort === sourcePort &&
          e.sourcePortName === sourcePortName && e.targetNodeId === targetNodeId) return;
    }
    const id = uid('e');
    const triggerMessageType = sourcePort === 'error' ? 'Fail' : 'Complete';
    State.edges.set(id, { id, sourceNodeId, sourcePort, sourcePortName, targetNodeId, targetPort, triggerMessageType });
    this.markDirty();
    Edges.render();
    const s = State.nodes.get(sourceNodeId)?.nodeName || '?';
    const t = State.nodes.get(targetNodeId)?.nodeName || '?';
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
    const existing = new Set(Object.values(State.workflows).map(w => w.workflowName));
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
      workflowId: id, workflowName: name,
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
    if (titleInput) titleInput.value = wf.workflowName;
    this.createNode('start', 240, 180);
    State.isDirty = false;
    State.dirtyWorkflows.delete(id);
    this.renderAll();
    if (!silent) toast('New workflow created', 'success');
  },

  async saveWorkflow() {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) return;
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    wf.workflowName = titleInput?.value || wf.workflowName || 'Untitled';
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
    wf.updatedAt = now();
    State.isDirty = false;
    if (State.currentWfId) State.dirtyWorkflows.delete(State.currentWfId);
    State.lastSaved = now();

    // Always try server save
    try {
      await saveWorkflowApi(wf.workflowName, wf);
      toast('Saved to server', 'success');
    } catch {
      toast('Saved locally', 'success');
    }

    this.renderAll();
    // Validate all nodes after save
    this.triggerValidation();
  },

  /** Switch to a workflow tab, prompting to save if current workflow is dirty */
  switchToWorkflow(id: string) {
    if (State.currentWfId === id) {
      this.loadWorkflow(id);
      return;
    }
    // Auto-snapshot current tab and switch — no prompt needed
    this.snapshotCurrentWorkflow();
    this.loadWorkflow(id);
  },

  /** Show a 3-button modal: Save / Discard / Cancel */
  showDirtyPrompt(workflowName: string, onSave: () => void, onDiscard: () => void) {
    const backdrop = document.createElement('div');
    backdrop.className = 'modal-backdrop';
    const modal = document.createElement('div');
    modal.className = 'modal';
    modal.innerHTML = `
      <h3>Unsaved changes</h3>
      <p>"${escapeHtml(workflowName)}" has unsaved changes. What would you like to do?</p>
      <div class="modal-actions">
        <button class="modal-btn-secondary" data-action="cancel">Cancel</button>
        <button class="modal-btn-danger" data-action="discard">Discard</button>
        <button class="modal-btn-primary" data-action="save">Save</button>
      </div>
    `;
    backdrop.appendChild(modal);
    document.body.appendChild(backdrop);

    modal.querySelector('[data-action="cancel"]')!.addEventListener('click', () => backdrop.remove());
    modal.querySelector('[data-action="discard"]')!.addEventListener('click', () => { backdrop.remove(); onDiscard(); });
    modal.querySelector('[data-action="save"]')!.addEventListener('click', () => { backdrop.remove(); onSave(); });
    backdrop.addEventListener('click', (e) => { if (e.target === backdrop) backdrop.remove(); });
  },

  loadWorkflow(id: string) {
    const wf = State.workflows[id];
    if (!wf) return;
    // Dispose editors from previous tab
    if (State.currentWfId && State.currentWfId !== id) {
      disposeAllEditors();
    }
    State.currentWfId = id;
    if (!State.openTabs.includes(id)) State.openTabs.push(id);
    // Normalize node types to canonical casing
    normalizeWorkflowNodeTypes(wf.nodes || []);
    // Rebuild parent-child relationships from parentId/parentSlot
    this.rebuildChildren(wf.nodes || []);
    State.nodes = new Map((wf.nodes || []).map(n => [n.nodeId, n]));
    State.edges = new Map((wf.edges || []).map(e => [e.id, e]));

    // Ensure container edges exist (parent→child, sibling chains, loop feedback)
    this.ensureAllContainerEdges();

    State.isDirty = State.dirtyWorkflows.has(id);
    State.selection = { kind: null, id: null };
    State.logs = [];
    State.activeRun = null;
    Exec.setStatus('idle');
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    if (titleInput) titleInput.value = wf.workflowName;
    this.renderAll();
    Canvas.fitToView();
    // Validate all nodes immediately on load
    this.triggerValidation();
    // Refresh run history if the history tab is active
    if (this._bottomTab === 'history') this.loadRunHistory();
  },

  /** Save current live state (nodes, edges, title) back into the workflow object */
  snapshotCurrentWorkflow() {
    if (!State.currentWfId) return;
    const wf = State.workflows[State.currentWfId];
    if (!wf) return;
    const titleInput = document.getElementById('wfTitleInput') as HTMLInputElement | null;
    if (titleInput) wf.workflowName = titleInput.value || wf.workflowName;
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
  },

  deleteWorkflow() {
    const wf = State.workflows[State.currentWfId || ''];
    if (!wf) return;
    this.showModal({
      title: 'Delete workflow?',
      body: `"${wf.workflowName}" will be permanently removed.`,
      confirmLabel: 'Delete',
      danger: true,
      onConfirm: async () => {
        try { await deleteWorkflowApi(wf.workflowName); } catch { /* server unavailable */ }
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
    wf.workflowName = titleInput?.value || wf.workflowName || 'Untitled';
    wf.nodes = [...State.nodes.values()];
    wf.edges = [...State.edges.values()];
    const data = JSON.stringify(wf, null, 2);
    const blob = new Blob([data], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${wf.workflowName.replace(/[^a-z0-9]+/gi, '_')}.flow.json`;
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
        // Migrate old schema if needed
        this.migrateWorkflow(data);
        if (!data.workflowId || !Array.isArray(data.nodes)) throw new Error('Invalid workflow JSON');
        if (State.workflows[data.workflowId]) data.workflowId = uid('wf');
        data.updatedAt = now();
        State.workflows[data.workflowId] = data;
        this.saveToStorage();
        this.loadWorkflow(data.workflowId);
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
    const filterInput = modal.querySelector('.open-dialog-filter') as HTMLInputElement | null;
    if (filterInput) {
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
    }

    // Click to open a workflow
    modal.querySelectorAll('.open-dialog-item').forEach(item => {
      item.addEventListener('click', async () => {
        const wfId = (item as HTMLElement).dataset.wfId!;
        backdrop.remove();
        try {
          const record = await getWorkflow(wfId) as { definition?: string; yaml?: string; sidecarJson?: string };
          const rawDef = record?.definition || record?.yaml;
          if (!rawDef) { toast('Empty workflow', 'error'); return; }
          const data = JSON.parse(rawDef);
          // Migrate old schema if needed
          this.migrateWorkflow(data);

          // The definition has engine-schema nodes (no positions).
          // The sidecar has UI layout. Merge them.
          if (record.sidecarJson) {
            try {
              const sidecar = JSON.parse(record.sidecarJson);
              if (sidecar.createdAt) data.createdAt = sidecar.createdAt;
              if (sidecar.updatedAt) data.updatedAt = sidecar.updatedAt;
              if (sidecar.author) data.author = sidecar.author;

              // Merge node positions from sidecar into nodes
              if (sidecar.nodePositions && Array.isArray(data.nodes)) {
                for (const node of data.nodes) {
                  const nid = node.nodeId || node.id;
                  const pos = sidecar.nodePositions[nid];
                  if (pos) {
                    node.x = pos.x ?? 0;
                    node.y = pos.y ?? 0;
                    node.width = pos.width ?? 220;
                    if (pos.height) node.height = pos.height;
                    if (pos.parentId) node.parentId = pos.parentId;
                    if (pos.parentSlot) node.parentSlot = pos.parentSlot;
                    if (pos.collapsed != null) node.collapsed = pos.collapsed;
                    if (pos.branches) node.branches = pos.branches;
                    if (pos.children) node.children = pos.children;
                  }
                }
              }

              // Merge edge labels from sidecar
              if (sidecar.edgeLabels && Array.isArray(data.edges)) {
                for (const edge of data.edges) {
                  const lbl = sidecar.edgeLabels[edge.id];
                  if (lbl) edge.label = lbl;
                }
              }
            } catch { /* ignore corrupt sidecar */ }
          }

          // Map engine-schema fields to UI fields
          if (data.connections && !data.edges) {
            // Engine connections don't have IDs — generate them
            data.edges = data.connections.map((c: Record<string, unknown>) => ({
              ...c,
              id: c.id || uid('e'),
            }));
            delete data.connections;
          }
          // Ensure all edges have IDs
          if (Array.isArray(data.edges)) {
            for (const e of data.edges) {
              if (!e.id) e.id = uid('e');
            }
          }
          // Map node config from engine schema
          if (Array.isArray(data.nodes)) {
            for (const n of data.nodes) {
              if (n.configuration && !n.config) {
                n.config = n.configuration;
                delete n.configuration;
              }
              if (n.x == null) n.x = 0;
              if (n.y == null) n.y = 0;
              if (!n.width) n.width = 220;
              if (!n.status) n.status = '';
              if (n.parentId === undefined) n.parentId = null;
              if (n.parentSlot === undefined) n.parentSlot = null;
            }
            // Extract embedded Group childNodes into flat node list
            const extractedNodes: typeof data.nodes = [];
            const extractedEdges: typeof data.edges = [];
            for (const n of data.nodes) {
              if (n.runtimeType?.toLowerCase() === 'group' && n.config?.childNodes && Array.isArray(n.config.childNodes)) {
                for (const child of n.config.childNodes) {
                  if (child.configuration && !child.config) {
                    child.config = child.configuration;
                    delete child.configuration;
                  }
                  child.parentId = n.nodeId;
                  child.parentSlot = '_body';
                  if (child.x == null) child.x = 0;
                  if (child.y == null) child.y = 0;
                  if (!child.width) child.width = 220;
                  if (!child.status) child.status = '';
                  extractedNodes.push(child);
                }
                // Extract child connections as edges
                if (Array.isArray(n.config.childConnections)) {
                  for (const conn of n.config.childConnections) {
                    extractedEdges.push({ ...conn, id: conn.id || uid('e') });
                  }
                }
                delete n.config.childNodes;
                delete n.config.childConnections;
              }
            }
            if (extractedNodes.length) data.nodes.push(...extractedNodes);
            if (extractedEdges.length) {
              if (!data.edges) data.edges = [];
              data.edges.push(...extractedEdges);
            }
            // Rebuild children arrays from parentId/parentSlot
            this.rebuildChildren(data.nodes);
          }
          if (!data.workflowId) data.workflowId = uid('wf');
          if (!data.workflowName) data.workflowName = wfId;
          if (!data.nodes) data.nodes = [];
          if (!data.edges) data.edges = [];
          data.updatedAt = data.updatedAt || now();
          data.createdAt = data.createdAt || now();
          // Preserve the workflow's own ID; use server filename ID as fallback
          const stableId = data.workflowId || wfId;
          data.workflowId = stableId;
          // Store server-side file ID so save can PUT back to the right path
          data._serverId = wfId;

          // Remove any stale entries with the same name but different ID (old regenerated IDs)
          for (const [existingId, existingWf] of Object.entries(State.workflows)) {
            if (existingId !== stableId && existingWf.workflowName === data.workflowName) {
              delete State.workflows[existingId];
              State.openTabs = State.openTabs.filter(t => t !== existingId);
            }
          }

          // If already open, just switch to that tab
          if (State.openTabs.includes(stableId)) {
            State.workflows[stableId] = data; // refresh with latest server data
            if (State.currentWfId !== stableId) {
              this.switchToWorkflow(stableId);
            } else {
              this.loadWorkflow(stableId);
            }
            this.saveToStorage();
            toast(`Switched to: ${data.workflowName}`, 'success');
            return;
          }

          State.workflows[stableId] = data;
          this.saveToStorage();
          this.switchToWorkflow(stableId);
          toast(`Opened: ${data.workflowName}`, 'success');
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
      const dirty = State.dirtyWorkflows.has(id);
      return `
        <div class="tab ${active ? 'active' : ''} ${dirty ? 'dirty' : ''}" data-tab-id="${id}">
          <span class="tab-name">${escapeHtml(wf.workflowName)}</span>
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
        this.switchToWorkflow((tab as HTMLElement).dataset.tabId!);
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
    const wf = State.workflows[id];
    const isActive = State.currentWfId === id;
    const isDirty = State.dirtyWorkflows.has(id);

    const doClose = () => {
      State.dirtyWorkflows.delete(id);
      State.openTabs.splice(State.openTabs.indexOf(id), 1);
      this.saveToStorage();
      if (isActive) {
        if (State.openTabs.length > 0) {
          const nextIdx = Math.min(idx, State.openTabs.length - 1);
          this.loadWorkflow(State.openTabs[nextIdx]);
        } else {
          this.newWorkflow(true);
        }
      } else {
        this.renderTabs();
      }
    };

    if (isDirty && wf) {
      // Show save/discard/cancel dialog
      const backdrop = document.createElement('div');
      backdrop.className = 'modal-backdrop';
      const modal = document.createElement('div');
      modal.className = 'modal';
      modal.innerHTML = `
        <h3>Unsaved changes</h3>
        <p>"${escapeHtml(wf.workflowName)}" has unsaved changes.</p>
        <div class="modal-actions" style="gap: 6px;">
          <button class="modal-btn-secondary" data-role="cancel">Cancel</button>
          <button class="modal-btn-secondary" data-role="discard" style="color: var(--err);">Discard</button>
          <button class="modal-btn-primary" data-role="save">Save & Close</button>
        </div>
      `;
      backdrop.appendChild(modal);
      document.body.appendChild(backdrop);

      modal.querySelector('[data-role="cancel"]')!.addEventListener('click', () => backdrop.remove());
      modal.querySelector('[data-role="discard"]')!.addEventListener('click', () => {
        backdrop.remove();
        // Reload from last saved state
        if (wf.nodes) {
          State.nodes = new Map(wf.nodes.map(n => [n.nodeId, n]));
          State.edges = new Map(wf.edges.map(e => [e.id, e]));
        }
        State.isDirty = false;
        State.dirtyWorkflows.delete(id);
        doClose();
      });
      modal.querySelector('[data-role="save"]')!.addEventListener('click', async () => {
        backdrop.remove();
        await this.saveWorkflow();
        doClose();
      });
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) backdrop.remove(); });
    } else {
      if (isActive) this.snapshotCurrentWorkflow();
      doClose();
    }
  },

  deleteWorkflowById(id: string) {
    const wf = State.workflows[id];
    if (!wf) return;
    if (!confirm(`Delete "${wf.workflowName}"?`)) return;
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
    State.openTabs = [];
  },

  /** No-op — server is the source of truth. Kept for API compat. */
  saveToStorage() {
  },

  /** Migrate a workflow object from old schema to new engine-aligned schema */
  /** Rebuild children arrays on parent nodes from parentId/parentSlot on child nodes */
  rebuildChildren(nodes: FlowNode[]) {
    // Legacy slot name aliases (old sidecar data may use these)
    const slotAliases: Record<string, string> = {
      'iterate': 'LoopBody',
      'loopbody': 'LoopBody',
      'loop_body': 'LoopBody',
    };

    // Build a lookup
    const byId = new Map<string, FlowNode>();
    for (const n of nodes) byId.set(n.nodeId, n);

    // Normalize slot names on all child nodes
    for (const n of nodes) {
      if (n.parentSlot) {
        const canonical = slotAliases[n.parentSlot.toLowerCase()];
        if (canonical) n.parentSlot = canonical;
      }
    }

    // Clear existing children on all container-type nodes
    for (const n of nodes) {
      if (n.children) {
        for (const slot of Object.keys(n.children)) {
          n.children[slot] = [];
        }
      }
    }
    // Rebuild from parentId/parentSlot
    for (const n of nodes) {
      if (n.parentId && n.parentSlot) {
        const parent = byId.get(n.parentId);
        if (parent) {
          ensureChildren(parent);
          if (!parent.children) parent.children = {};
          if (!parent.children[n.parentSlot]) parent.children[n.parentSlot] = [];
          if (!parent.children[n.parentSlot].includes(n.nodeId)) {
            parent.children[n.parentSlot].push(n.nodeId);
          }
        }
      }
    }
  },

  migrateWorkflow(wf: Record<string, unknown>) {
    // Workflow-level renames
    if ('id' in wf && !('workflowId' in wf)) { wf.workflowId = wf.id; delete wf.id; }
    if ('name' in wf && !('workflowName' in wf)) { wf.workflowName = wf.name; delete wf.name; }
    if ('purpose' in wf && !('description' in wf)) { wf.description = wf.purpose; delete wf.purpose; }

    // Migrate nodes
    const nodes = wf.nodes as Record<string, unknown>[] | undefined;
    if (Array.isArray(nodes)) {
      for (const n of nodes) {
        if ('id' in n && !('nodeId' in n)) { n.nodeId = n.id; delete n.id; }
        if ('type' in n && !('runtimeType' in n)) { n.runtimeType = n.type; delete n.type; }
        if ('name' in n && !('nodeName' in n)) { n.nodeName = n.name; delete n.name; }
      }
    }

    // Migrate edges
    const edges = wf.edges as Record<string, unknown>[] | undefined;
    if (Array.isArray(edges)) {
      for (const e of edges) {
        if ('source' in e && !('sourceNodeId' in e)) { e.sourceNodeId = e.source; delete e.source; }
        if ('target' in e && !('targetNodeId' in e)) { e.targetNodeId = e.target; delete e.target; }
        if (!e.id) e.id = uid('e');
      }
    }

    // Also handle connections → edges rename
    if ('connections' in wf && !('edges' in wf)) {
      const conns = wf.connections as Record<string, unknown>[];
      if (Array.isArray(conns)) {
        for (const c of conns) { if (!c.id) c.id = uid('e'); }
        wf.edges = conns;
        delete wf.connections;
      }
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
    count.textContent = State.logs.length > 0 ? String(State.logs.length) : '';
    if (!State.logs.length) {
      body.innerHTML = `<div class="log-empty">No execution logs yet. Hit Run to start.</div>`;
      return;
    }

    // Merge consecutive NodeLog entries (🤖) from the same node into grouped blocks
    const merged: typeof State.logs = [];
    for (const entry of State.logs) {
      const prev = merged.length > 0 ? merged[merged.length - 1] : null;
      const isAgentLog = entry.msg.startsWith('🤖 ');
      const prevIsAgentLog = prev?.msg.startsWith('🤖 ');

      if (isAgentLog && prevIsAgentLog && entry.nodeId && entry.nodeId === prev?.nodeId) {
        // Merge into previous entry
        const prevMsg = prev.msg;
        const newLine = entry.msg.replace(/^🤖 \[[^\]]*\]\s*/, '');
        prev.msg = prevMsg + '\n' + newLine;
      } else {
        merged.push({ ...entry });
      }
    }

    body.innerHTML = merged.slice().reverse().map(e => {
      const nodeName = e.nodeId ? (State.nodes.get(e.nodeId)?.nodeName || e.nodeId) : 'system';
      const isMultiLine = e.msg.includes('\n');
      if (isMultiLine) {
        // Render merged agent log as a collapsible block
        const lines = e.msg.split('\n');
        const header = lines[0];
        const details = lines.slice(1)
          .map(l => `<div class="log-agent-line">${escapeHtml(l)}</div>`)
          .join('');
        return `<div class="log-line log-agent-group">
          <span class="log-time">${escapeHtml(e.t)}</span>
          <span class="log-level ${e.level}">${escapeHtml(e.level)}</span>
          <span class="log-node">[${escapeHtml(nodeName)}]</span>
          <span class="log-msg">${escapeHtml(header)}</span>
          <details class="log-agent-details"><summary>${lines.length - 1} tool calls</summary>${details}</details>
        </div>`;
      }
      return `<div class="log-line">
        <span class="log-time">${escapeHtml(e.t)}</span>
        <span class="log-level ${e.level}">${escapeHtml(e.level)}</span>
        <span class="log-node">[${escapeHtml(nodeName)}]</span>
        <span class="log-msg">${escapeHtml(e.msg)}</span>
      </div>`;
    }).join('');
  },

  // ---------- BOTTOM PANEL TABS ----------
  _bottomTab: 'log' as 'log' | 'history',

  switchBottomTab(tab: 'log' | 'history') {
    this._bottomTab = tab;
    const logBody = document.getElementById('logsBody');
    const histBody = document.getElementById('historyBody');
    const clearBtn = document.querySelector('[data-action="clear-logs"]') as HTMLElement | null;
    const refreshBtn = document.getElementById('btnRefreshHistory');
    document.querySelectorAll('.bottom-tab').forEach(t => {
      t.classList.toggle('active', (t as HTMLElement).dataset.bottomTab === tab);
    });
    if (logBody) logBody.style.display = tab === 'log' ? '' : 'none';
    if (histBody) histBody.style.display = tab === 'history' ? '' : 'none';
    if (clearBtn) clearBtn.style.display = tab === 'log' ? '' : 'none';
    if (refreshBtn) refreshBtn.style.display = tab === 'history' ? '' : 'none';
    if (tab === 'history') this.loadRunHistory();
  },

  async loadRunHistory() {
    const body = document.getElementById('historyBody');
    const countEl = document.getElementById('historyCount');
    if (!body) return;
    const wf = State.workflows[State.currentWfId || ''];
    const wfName = wf?.workflowName;
    try {
      const runs = await listRuns(wfName);
      if (countEl) countEl.textContent = runs.length > 0 ? String(runs.length) : '';
      if (!runs.length) {
        body.innerHTML = `<div class="log-empty">No runs for "${escapeHtml(wfName || 'this workflow')}".</div>`;
        return;
      }
      runs.sort((a, b) => new Date(b.startTimeUtc).getTime() - new Date(a.startTimeUtc).getTime());
      body.innerHTML = `<table class="history-table">
        <thead><tr>
          <th>Status</th><th>Started</th><th>Duration</th><th>Run ID</th><th></th>
        </tr></thead>
        <tbody>${runs.map(r => this.renderRunRow(r)).join('')}</tbody>
      </table>`;
    } catch {
      body.innerHTML = '<div class="log-empty">Could not load run history from server.</div>';
      if (countEl) countEl.textContent = '';
    }
  },

  renderRunRow(r: { runId: string; workflowId?: string; workflowName?: string; status: string; startTimeUtc: string; endTimeUtc?: string }) {
    const status = r.status.toLowerCase();
    const started = new Date(r.startTimeUtc);
    const startStr = started.toLocaleTimeString('en-US', { hour12: false }) + ' ' + started.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    let duration = '—';
    if (r.endTimeUtc) {
      const ms = new Date(r.endTimeUtc).getTime() - started.getTime();
      if (ms < 1000) duration = `${ms}ms`;
      else if (ms < 60000) duration = `${(ms / 1000).toFixed(1)}s`;
      else duration = `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
    } else if (status === 'running') {
      duration = '⏳';
    }
    const shortId = r.runId.substring(0, 8);
    return `<tr data-run-id="${escapeHtml(r.runId)}">
      <td><span class="run-status ${status}"><span class="dot"></span>${status}</span></td>
      <td>${startStr}</td>
      <td class="run-duration">${duration}</td>
      <td class="run-id" title="${escapeHtml(r.runId)}">${shortId}…</td>
      <td class="run-actions">
        <button class="run-replay-btn" data-action="view-run" data-run-id="${escapeHtml(r.runId)}" title="View on canvas">
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>
          View
        </button>
        <button class="run-replay-btn" data-action="replay-run" data-run-id="${escapeHtml(r.runId)}" title="Replay in log">Log</button>
      </td>
    </tr>`;
  },

  async viewRunOnCanvas(runId: string) {
    Exec.reset();
    this.log('info', null, `Loading run ${runId.substring(0, 8)}…`);
    try {
      const res = await fetch(`/api/runs/${runId}`);
      if (!res.ok) throw new Error(`Failed to fetch run: ${res.status}`);
      const data = await res.json();
      const events: Array<{ kind: string; nodeId?: string; payload?: Record<string, unknown>; timestampUtc: string; synthetic?: boolean }> = data.events || [];

      // Build per-node summary from events
      const nodeInfo = new Map<string, { status: string; duration?: string; error?: string; startTime?: string; endTime?: string; nodeInstanceId?: string; reason?: string; inputData?: Record<string, unknown>; outputData?: Record<string, unknown>; globalVariables?: Record<string, unknown>; startCount?: number }>();
      for (const evt of events) {
        if (!evt.nodeId) continue;
        const isSynthetic = evt.synthetic;
        switch (evt.kind) {
          case 'NodeStarted': {
            const info = nodeInfo.get(evt.nodeId) || { status: 'running' };
            info.status = 'running';
            info.startTime = evt.timestampUtc;
            info.nodeInstanceId = evt.payload?.nodeInstanceId as string;
            info.startCount = (info.startCount || 0) + 1;
            nodeInfo.set(evt.nodeId, info);
            break;
          }
          case 'NodeCompleted': {
            const info = nodeInfo.get(evt.nodeId) || { status: 'success' };
            info.status = 'success';
            info.duration = evt.payload?.duration as string || info.duration || '';
            info.endTime = evt.timestampUtc;
            // Synthetic events carry context data
            if (isSynthetic && evt.payload) {
              if (evt.payload.inputData) info.inputData = evt.payload.inputData as Record<string, unknown>;
              if (evt.payload.outputData) info.outputData = evt.payload.outputData as Record<string, unknown>;
              if (evt.payload.globalVariables) info.globalVariables = evt.payload.globalVariables as Record<string, unknown>;
            }
            nodeInfo.set(evt.nodeId, info);
            break;
          }
          case 'NodeFailed': {
            const info = nodeInfo.get(evt.nodeId) || { status: 'failed' };
            info.status = 'failed';
            info.error = evt.payload?.errorMessage as string || info.error || '';
            info.duration = evt.payload?.duration as string || info.duration || '';
            info.endTime = evt.timestampUtc;
            if (isSynthetic && evt.payload) {
              if (evt.payload.inputData) info.inputData = evt.payload.inputData as Record<string, unknown>;
              if (evt.payload.globalVariables) info.globalVariables = evt.payload.globalVariables as Record<string, unknown>;
            }
            nodeInfo.set(evt.nodeId, info);
            break;
          }
          case 'NodeCancelled': {
            const info = nodeInfo.get(evt.nodeId) || { status: 'cancelled' };
            info.status = 'cancelled';
            info.reason = evt.payload?.reason as string || '';
            info.duration = evt.payload?.duration as string || info.duration || '';
            info.endTime = evt.timestampUtc;
            nodeInfo.set(evt.nodeId, info);
            break;
          }
        }
      }

      // Synthesize runtime data for Group child nodes from ChildResults
      for (const [nodeId, info] of nodeInfo) {
        const node = State.nodes.get(nodeId);
        if (!node || node.runtimeType.toLowerCase() !== 'group') continue;
        const childResults = info.outputData?.ChildResults as Record<string, Record<string, unknown>> | undefined;
        if (!childResults) continue;
        for (const [childId, childOut] of Object.entries(childResults)) {
          if (nodeInfo.has(childId)) continue; // don't overwrite direct events
          nodeInfo.set(childId, {
            status: 'success',
            startTime: info.startTime,
            endTime: info.endTime,
            duration: info.duration,
            outputData: childOut,
          });
        }
      }

      // Apply statuses and runtime data to nodes
      State.nodes.forEach(n => {
        const info = nodeInfo.get(n.nodeId);
        if (info) {
          n.status = info.status as '' | 'running' | 'success' | 'failed' | 'cancelled';
          n._runDuration = info.duration || undefined;
          n._runError = info.error || undefined;
          n._iterCount = (info.startCount && info.startCount > 1) ? info.startCount - 1 : undefined;
          n._runData = {
            status: info.status,
            nodeInstanceId: info.nodeInstanceId,
            startTime: info.startTime,
            endTime: info.endTime,
            duration: info.duration,
            ...(info.error ? { errorMessage: info.error } : {}),
            ...(info.reason ? { cancelReason: info.reason } : {}),
            ...(info.inputData ? { inputData: info.inputData } : {}),
            ...(info.outputData ? { outputData: info.outputData } : {}),
            ...(info.globalVariables ? { globalVariables: info.globalVariables } : {}),
          };
        } else {
          n.status = '';
          n._runDuration = undefined;
          n._runError = undefined;
          n._runData = undefined;
          n._iterCount = undefined;
        }
      });

      // Activate edges that were actually traversed and attach runtime data
      State.edges.forEach(e => {
        const srcInfo = nodeInfo.get(e.sourceNodeId);
        if (!srcInfo) {
          e._active = false;
          e._runtimeData = undefined;
          return;
        }
        if (srcInfo.status === 'success') {
          e._active = e.sourcePort !== 'error';
          if (e._active) {
            const srcNode = State.nodes.get(e.sourceNodeId);
            e._runtimeData = {
              messageType: 'NodeCompleteMessage',
              sourceNodeId: e.sourceNodeId,
              sourceNodeName: srcNode?.nodeName || e.sourceNodeId,
              timestamp: srcInfo.startTime,
              duration: srcInfo.duration,
              status: 'Completed',
            };
          } else {
            e._runtimeData = undefined;
          }
        } else if (srcInfo.status === 'failed') {
          e._active = e.sourcePort === 'error';
          if (e._active) {
            const srcNode = State.nodes.get(e.sourceNodeId);
            e._runtimeData = {
              messageType: 'NodeFailMessage',
              sourceNodeId: e.sourceNodeId,
              sourceNodeName: srcNode?.nodeName || e.sourceNodeId,
              errorMessage: srcInfo.error || 'Unknown error',
              timestamp: srcInfo.startTime,
              status: 'Failed',
            };
          } else {
            e._runtimeData = undefined;
          }
        } else {
          e._active = false;
          e._runtimeData = undefined;
        }
      });

      // Set exec status and store run info
      const wfStatus = data.status?.toLowerCase() || 'idle';
      Exec.setStatus(wfStatus === 'completed' ? 'success' : wfStatus === 'failed' ? 'failed' : wfStatus === 'cancelled' ? 'cancelled' : 'idle');

      // Get final global variables from the last synthetic event
      const lastGlobals = [...events].reverse().find(e => e.synthetic && e.payload?.globalVariables);
      const wfCompleted = events.find(e => e.kind === 'WorkflowCompleted' || e.kind === 'WorkflowFailed');

      State.activeRun = {
        runId,
        status: data.status,
        startTime: data.startTimeUtc,
        endTime: data.endTimeUtc,
        duration: (wfCompleted?.payload?.duration as string) || undefined,
        globalVariables: (lastGlobals?.payload?.globalVariables as Record<string, unknown>) || undefined,
      };

      Nodes.render();
      Edges.render();
      // Re-render inspector to show run data
      Inspector.render();
      this.log('success', null, `Run ${runId.substring(0, 8)}: ${data.status} (${nodeInfo.size} nodes)`);

      // Log per-node details
      for (const [nid, info] of nodeInfo) {
        const name = State.nodes.get(nid)?.nodeName || nid;
        if (info.status === 'failed') {
          this.log('error', nid, `✗ ${name}: ${info.error || 'failed'}`);
        } else if (info.status === 'success' && info.duration) {
          this.log('success', nid, `✓ ${name} (${info.duration})`);
        }
      }
    } catch (err) {
      this.log('error', null, `View run failed: ${(err as Error).message}`);
    }
  },

  async replayRun(runId: string) {
    this.switchBottomTab('log');
    this.clearLogs();
    Exec.reset();
    this.log('info', null, `Replaying run ${runId.substring(0, 8)}…`);
    try {
      const { replayRunEvents } = await import('./exec');
      await replayRunEvents(runId, (level, nodeId, msg) => this.log(level, nodeId, msg));
      this.log('success', null, 'Replay complete');
    } catch (err) {
      this.log('error', null, `Replay failed: ${(err as Error).message}`);
    }
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
      if (ids.has(e.sourceNodeId) && ids.has(e.targetNodeId)) {
        edges.push(clone(e));
      }
    });

    this._clipboard = { nodes, edges };
    toast(`Copied ${nodes.length} node${nodes.length > 1 ? 's' : ''}`, 'info');
  },

  cutSelected() {
    if (State.selection.kind !== 'node' || !State.selection.id) return;
    if (State.isLocked) return;
    this.copySelected();
    this.deleteNode(State.selection.id!);
    toast('Cut to clipboard', 'info');
  },

  pasteClipboard() {
    if (!this._clipboard) return;
    const { nodes, edges } = this._clipboard;

    // Build old→new ID mapping
    const idMap = new Map<string, string>();
    nodes.forEach(n => idMap.set(n.nodeId, uid('n')));
    edges.forEach(e => idMap.set(e.id, uid('e')));

    // Paste offset so pasted nodes don't overlap originals
    const offset = 40;

    // Create new nodes with remapped IDs
    nodes.forEach(n => {
      const newNode: FlowNode = {
        ...clone(n),
        nodeId: idMap.get(n.nodeId)!,
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
      State.nodes.set(newNode.nodeId, newNode);
    });

    // Create new edges with remapped IDs
    edges.forEach(e => {
      const newEdge: FlowEdge = {
        ...clone(e),
        id: idMap.get(e.id)!,
        sourceNodeId: idMap.get(e.sourceNodeId) || e.sourceNodeId,
        targetNodeId: idMap.get(e.targetNodeId) || e.targetNodeId,
      };
      State.edges.set(newEdge.id, newEdge);
    });

    this.markDirty();
    // Select the root pasted node
    const newRootId = idMap.get(nodes[0].nodeId);
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
      if ((e.ctrlKey || e.metaKey) && e.key === 'x') { e.preventDefault(); this.cutSelected(); }
      if ((e.ctrlKey || e.metaKey) && e.key === 'v') { if (!State.isLocked) this.pasteClipboard(); }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        if (State.isLocked) return;
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
        case 'toggle-lock': this.toggleLock(); break;
        case 'toggle-palette': this.togglePanel('palette'); break;
        case 'toggle-inspector': this.togglePanel('inspector'); break;
        case 'toggle-logs': this.togglePanel('logs'); break;
        case 'toggle-inspector-fullscreen': {
          const el = document.getElementById('inspector');
          if (el) el.classList.toggle('fullscreen');
          break;
        }
        case 'toggle-logs-fullscreen': {
          const el = document.querySelector('.logs');
          if (el) el.classList.toggle('fullscreen');
          break;
        }
        case 'clear-logs': this.clearLogs(); break;
        case 'refresh-history': this.loadRunHistory(); break;
        case 'replay-run': {
          const runId = btn.dataset.runId || btn.closest('[data-run-id]')?.getAttribute('data-run-id');
          if (runId) this.replayRun(runId);
          break;
        }
        case 'view-run': {
          const runId = btn.dataset.runId || btn.closest('[data-run-id]')?.getAttribute('data-run-id');
          if (runId) this.viewRunOnCanvas(runId);
          break;
        }
      }
    });

    // Bottom panel tab switching
    document.addEventListener('click', (e) => {
      const tab = (e.target as HTMLElement).closest('.bottom-tab') as HTMLElement | null;
      if (!tab) return;
      const tabName = tab.dataset.bottomTab as 'log' | 'history';
      if (tabName) this.switchBottomTab(tabName);
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
