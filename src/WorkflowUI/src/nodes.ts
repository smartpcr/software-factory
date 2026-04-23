// -----------------------------------------------------------------------
// Node DOM rendering and interaction handlers
// -----------------------------------------------------------------------

import type { FlowNode, AppCallbacks } from './types';
import { NodeTypes } from './registry';
import { State } from './state';
import { getSlots, ensureChildren, escapeHtml } from './utils';
import { Edges } from './edges';
import { Minimap } from './minimap';

let callbacks: AppCallbacks;

export const Nodes = {
  init(cb: AppCallbacks) { callbacks = cb; },

  render() {
    const layer = document.getElementById('nodesLayer');
    if (!layer) return;
    const topLevel = [...State.nodes.values()].filter(n => !n.parentId);
    const presentIds = new Set(topLevel.map(n => n.nodeId));
    layer.querySelectorAll(':scope > .node').forEach(el => {
      const e = el as HTMLElement;
      if (!presentIds.has(e.dataset.id || '')) el.remove();
    });
    topLevel.forEach(node => {
      let el = layer.querySelector(`:scope > .node[data-id="${node.nodeId}"]`) as HTMLElement | null;
      if (!el) {
        el = this.createElement(node, false);
        layer.appendChild(el);
      } else {
        // If the node was rendered with an unknown type (empty), rebuild it fully
        const hasContent = el.querySelector(':scope > .node-head');
        if (!hasContent) {
          const newEl = this.createElement(node, false);
          layer.replaceChild(newEl, el);
          el = newEl;
        } else {
          this.refreshElement(el, node, false);
        }
      }
      this.renderChildrenInto(el, node);
    });
  },

  renderChildrenInto(parentEl: HTMLElement, parentNode: FlowNode) {
    const slots = getSlots(parentNode);
    if (!slots.length) return;
    ensureChildren(parentNode);
    slots.forEach(slot => {
      const slotEl = this.findSlotEl(parentEl, parentNode, slot);
      if (!slotEl) return;
      const childIds = (parentNode.children && parentNode.children[slot]) || [];
      slotEl.innerHTML = '';
      if (!childIds.length) {
        const empty = document.createElement('div');
        empty.className = parentNode.runtimeType === 'container' ? 'container-hint' : 'branch-port-empty';
        empty.textContent = 'Drop component';
        slotEl.appendChild(empty);
      } else {
        slotEl.appendChild(this.makeGapSlot(parentNode.nodeId, slot, 0));
        childIds.forEach((cid, i) => {
          const child = State.nodes.get(cid);
          if (!child) return;
          const childEl = this.createElement(child, true);
          slotEl.appendChild(childEl);
          this.renderChildrenInto(childEl, child);
          if (i < childIds.length - 1) {
            slotEl.appendChild(this.makeSiblingConnector());
          }
          slotEl.appendChild(this.makeGapSlot(parentNode.nodeId, slot, i + 1));
        });
      }
    });
  },

  makeSiblingConnector(): HTMLElement {
    const el = document.createElement('div');
    el.className = 'sibling-connector';
    el.innerHTML = '<svg width="12" height="18" viewBox="0 0 12 18" aria-hidden="true">' +
      '<path d="M6 0 V 12" stroke="currentColor" stroke-width="1.4" fill="none"/>' +
      '<path d="M2.5 11 L 6 16 L 9.5 11" stroke="currentColor" stroke-width="1.4" fill="none" stroke-linecap="round" stroke-linejoin="round"/>' +
      '</svg>';
    return el;
  },

  makeGapSlot(parentId: string, slot: string, index: number): HTMLElement {
    const gap = document.createElement('div');
    gap.className = 'gap-drop-slot';
    gap.dataset.parentId = parentId;
    gap.dataset.slot = slot;
    gap.dataset.index = String(index);
    gap.addEventListener('dragover', (e: DragEvent) => {
      if (!e.dataTransfer?.types.includes('text/flow-node')) return;
      e.preventDefault();
      e.stopPropagation();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
      gap.classList.add('drop-active');
    });
    gap.addEventListener('dragleave', () => gap.classList.remove('drop-active'));
    gap.addEventListener('drop', (e: DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      gap.classList.remove('drop-active');
      const type = e.dataTransfer?.getData('text/flow-node');
      if (!type) return;
      callbacks.createChildNode(type, parentId, slot, index);
    });
    return gap;
  },

  findSlotEl(parentEl: HTMLElement, parentNode: FlowNode, slot: string): HTMLElement | null {
    if (parentNode.runtimeType === 'container' && slot === '_body') {
      return parentEl.querySelector(':scope > .container-body > .container-children');
    }
    return parentEl.querySelector(
      `:scope > .branch-ports > .branch-port[data-branch="${CSS.escape(slot)}"] > .branch-port-children`
    );
  },

  createElement(node: FlowNode, isNested: boolean): HTMLElement {
    const el = document.createElement('div');
    el.className = 'node' + (isNested ? ' nested' : '');
    el.dataset.id = node.nodeId;
    el.title = this.nodeTooltip(node);
    el.innerHTML = this.html(node);
    this.refreshElement(el, node, isNested);
    this.attachHandlers(el, node, isNested);
    return el;
  },

  refreshElement(el: HTMLElement, node: FlowNode, isNested: boolean) {
    const type = NodeTypes[node.runtimeType];
    if (!type) return;
    if (!isNested) {
      el.style.left = node.x + 'px';
      el.style.top = node.y + 'px';
    }
    el.style.color = type.color;
    el.dataset.status = node.status || '';
    el.classList.toggle('selected', State.selection.kind === 'node' && State.selection.id === node.nodeId);
    if (node.runtimeType === 'container') {
      el.classList.add('container');
      el.classList.toggle('collapsed', !!node.collapsed);
    }
    const headTitle = el.querySelector(':scope > .node-head > .node-title');
    if (headTitle) headTitle.textContent = node.nodeName || type.label;
    // Only update body content, not the error port/drop zones
    const bodyContentEl = el.querySelector(':scope > .node-body > .node-body-content');
    if (bodyContentEl) bodyContentEl.innerHTML = this.bodyHtml(node);
    // Validation badge
    const hasErrors = node.validationErrors && node.validationErrors.length > 0;
    el.classList.toggle('has-validation-error', !!hasErrors);
    let badge = el.querySelector(':scope > .validation-badge') as HTMLElement | null;
    if (hasErrors) {
      if (!badge) {
        badge = document.createElement('div');
        badge.className = 'validation-badge';
        el.appendChild(badge);
      }
      badge.innerHTML = `<svg width="12" height="12" viewBox="0 0 24 24" fill="var(--err)" stroke="var(--bg-1)" stroke-width="2"><path d="M12 2L2 22h20L12 2z"/><path d="M12 9v4M12 17h.01" stroke="var(--bg-1)" fill="none" stroke-width="2" stroke-linecap="round"/></svg>`;
      badge.setAttribute('data-tooltip', node.validationErrors!.join('\n'));
    } else if (badge) {
      badge.remove();
    }
  },

  html(node: FlowNode): string {
    const type = NodeTypes[node.runtimeType];
    if (!type) return '';
    const ports = type.ports;
    const labels = type.portLabels;
    let portsHtml = '';
    if (ports.in) {
      const tip = escapeHtml(labels?.in || 'in');
      portsHtml += `<div class="port in" data-port-kind="in" data-node-id="${node.nodeId}" data-tooltip="${tip}"></div>`;
    }
    if (ports.out) {
      const tip = escapeHtml(labels?.out || 'out');
      portsHtml += `<div class="port out" data-port-kind="out" data-node-id="${node.nodeId}" data-tooltip="${tip}"></div>`;
    }
    const errorTip = escapeHtml(labels?.error || 'onError');
    const errorPortHtml = ports.error
      ? `<div class="port error" data-port-kind="error" data-node-id="${node.nodeId}" data-tooltip="${errorTip}"></div>`
      : '';

    const branchesHtml = type.branches ? this.branchHtml(node) : '';
    const collapseBtn = node.runtimeType === 'container'
      ? `<button class="collapse-btn" data-action="toggle-collapse" data-node-id="${node.nodeId}"><svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M6 9l6 6 6-6"/></svg></button>`
      : '';
    const typeLabel = `<span class="node-type-label mono">${node.runtimeType}</span>`;
    const dropAbove = ports.in ? `<div class="drop-zone above" data-node-id="${node.nodeId}" data-position="before"></div>` : '';
    const dropBelow = (ports.out || type.branches) ? `<div class="drop-zone below" data-node-id="${node.nodeId}" data-position="after"></div>` : '';
    const dropError = ports.error ? `<div class="drop-zone error-right" data-node-id="${node.nodeId}" data-position="after-error"></div>` : '';
    const safeName = escapeHtml(node.nodeName || type.label);

    const bodyContent = node.runtimeType.toLowerCase() === 'container'
      ? `<div class="container-body"><div class="container-children"></div></div>`
      : `<div class="node-body"><div class="node-body-content">${this.bodyHtml(node)}</div></div>`;

    const durationBadge = node._runDuration
      ? `<div class="node-duration-badge" title="${escapeHtml(node._runError || '')}">${escapeHtml(node._runDuration)}</div>`
      : '';

    return `
      ${dropAbove}
      <div class="node-status-badge">${node.status || ''}</div>
      ${durationBadge}
      <div class="node-head">
        <div class="node-head-accent"></div>
        <div class="node-icon">${type.icon}</div>
        <div class="node-title">${safeName}</div>
        ${collapseBtn || typeLabel}
      </div>
      ${bodyContent}
      ${portsHtml}
      ${errorPortHtml}
      ${dropError}
      ${branchesHtml}
      ${dropBelow}
    `;
  },

  bodyHtml(node: FlowNode): string {
    const c = node.config || {};
    const rt = node.runtimeType.toLowerCase();
    const row = (label: string, value: string, mono = false) =>
      `<div class="node-body-row"><span class="lab">${escapeHtml(label)}</span><span class="val${mono ? ' mono' : ''}">${escapeHtml(value)}</span></div>`;

    if (rt === 'start') {
      const trigLabel = String(c.triggerType || 'Manual');
      let detail = trigLabel;
      if (trigLabel === 'Schedule' && c.schedule) detail = String(c.schedule);
      if (trigLabel === 'Event' && c.eventName) detail = String(c.eventName);
      if (trigLabel === 'WebHook' && c.webHookRoute) detail = `/webhook/${String(c.webHookRoute)}`;
      return row('Trigger', detail);
    }
    if (rt === 'trigger') return row('Method', String(c.method || 'POST')) + row('Route', String(c.route || '—'));
    if (rt === 'if' || rt === 'ifelse') return row('Condition', String(c.condition || '—'), true);
    if (rt === 'switch') return row('Expression', String(c.expression || '—'), true);
    if (rt === 'foreach') return row('Mode', String(c.mode || 'foreach')) + row('Source', String(c.source || '—'), true);
    if (rt === 'ai') {
      const m = String(c.mode || 'skill').toLowerCase();
      const binding = m === 'skill' ? String(c.skillName || '—') : m === 'subagent' ? String(c.subAgentName || '—') : m === 'prompt' ? 'prompt' : '—';
      return row('Mode', m) + row(m === 'skill' ? 'Skill' : m === 'subagent' ? 'Agent' : 'Prompt', binding);
    }
    if (rt === 'http' || rt === 'httprequest') return row('Method', String(c.method || 'GET')) + row('URL', String(c.url || '—'));
    if (rt === 'delay') return row('Duration', `${String(c.duration || 0)}${String(c.unit || 'ms')}`);
    if (rt === 'subflow') {
      const refId = String(c.workflowId || '');
      const refWf = refId ? State.workflows[refId] : null;
      const name = refWf?.workflowName || refId || '—';
      return row('Workflow', name) + row('ID', refId || '—', true);
    }
    if (rt === 'action') return row('Action', String(c.actionType || '—')) + row('Handler', String(c.handler || '—'));
    if (rt === 'transform') return row('Expression', String(c.expression || '—'), true);
    if (rt === 'log') {
      const msg = String(c.messageTemplate || c.message || '—');
      const truncated = msg.length > 30 ? msg.slice(0, 28) + '…' : msg;
      return row('Level', String(c.level || 'Info')) + row('Message', truncated);
    }
    if (rt === 'parallel') return row('Join', String(c.joinMode || 'all'));
    if (rt === 'end') return row('Status', String(c.status || 'success'));
    if (node.description) return row('Description', node.description);
    return '';
  },

  /** Build a tooltip string for a node with metadata */
  nodeTooltip(node: FlowNode): string {
    const c = node.config || {};
    const rt = node.runtimeType.toLowerCase();
    const parts: string[] = [];
    parts.push(`Type: ${node.runtimeType}`);
    parts.push(`ID: ${node.nodeId}`);
    if (node.description) parts.push(`Description: ${node.description}`);
    if (rt === 'subflow') {
      const refId = String(c.workflowId || '');
      const refWf = refId ? State.workflows[refId] : null;
      if (refWf) {
        parts.push(`Workflow: ${refWf.workflowName}`);
        if ((refWf as Record<string, unknown>)._serverId) parts.push(`Path: workflows/${(refWf as Record<string, unknown>)._serverId}.json`);
        if (refWf.createdAt) parts.push(`Created: ${refWf.createdAt}`);
        if (refWf.updatedAt) parts.push(`Modified: ${refWf.updatedAt}`);
        if ((refWf as Record<string, unknown>).author) parts.push(`Author: ${(refWf as Record<string, unknown>).author}`);
      }
    }
    return parts.join('\n');
  },

  branchHtml(node: FlowNode): string {
    const type = NodeTypes[node.runtimeType];
    const branches = node.branches || type?.branches || [];
    const branchLabels = type?.portLabels?.branches;
    return `<div class="branch-ports">${
      branches.map(b => {
        const tip = escapeHtml(branchLabels?.[b] || b);
        return `
        <div class="branch-port" data-branch="${escapeHtml(b)}" data-parent-id="${node.nodeId}">
          <div class="branch-port-label">${escapeHtml(b)}</div>
          <div class="branch-port-children"></div>
        </div>
      `;
      }).join('')
    }</div>`;
  },

  attachHandlers(el: HTMLElement, node: FlowNode, isNested: boolean) {
    if (!isNested) {
      let dragging = false;
      let startPos = { x: 0, y: 0 };
      let nodeStart = { x: 0, y: 0 };
      let lastHover: HTMLElement | null = null;

      const clearHover = () => {
        if (lastHover) { lastHover.classList.remove('reparent-target'); lastHover = null; }
      };
      const findSlotAt = (clientX: number, clientY: number) => {
        const target = document.elementFromPoint(clientX, clientY) as HTMLElement | null;
        if (!target) return null;
        const branchEl = target.closest('.branch-port[data-parent-id]') as HTMLElement | null;
        if (branchEl) {
          const parentEl = branchEl.closest('.node') as HTMLElement | null;
          const parentId = parentEl?.dataset.id;
          if (parentId && parentId !== node.nodeId && !callbacks.isDescendantOf(parentId, node.nodeId)) {
            return { el: branchEl, parentId, slot: branchEl.dataset.branch || '' };
          }
        }
        const bodyEl = target.closest('.container-body') as HTMLElement | null;
        if (bodyEl) {
          const parentEl = bodyEl.closest('.node.container') as HTMLElement | null;
          const parentId = parentEl?.dataset.id;
          if (parentId && parentId !== node.nodeId && !callbacks.isDescendantOf(parentId, node.nodeId)) {
            return { el: bodyEl, parentId, slot: '_body' };
          }
        }
        return null;
      };

      el.addEventListener('pointerdown', (e: PointerEvent) => {
        const t = e.target as HTMLElement;
        if (t.classList.contains('port')) return;
        if (t.closest('.collapse-btn')) return;
        if (t.classList.contains('drop-zone')) return;
        e.stopPropagation();
        callbacks.select('node', node.nodeId);
        if (State.isLocked) return; // Lock prevents dragging
        dragging = true;
        startPos = { x: e.clientX, y: e.clientY };
        nodeStart = { x: node.x, y: node.y };
        el.classList.add('dragging');
        el.setPointerCapture(e.pointerId);
      });
      el.addEventListener('pointermove', (e: PointerEvent) => {
        if (!dragging) return;
        const dx = (e.clientX - startPos.x) / State.canvas.zoom;
        const dy = (e.clientY - startPos.y) / State.canvas.zoom;
        node.x = nodeStart.x + dx;
        node.y = nodeStart.y + dy;
        el.style.left = node.x + 'px';
        el.style.top = node.y + 'px';
        const hit = findSlotAt(e.clientX, e.clientY);
        if (hit && hit.el !== lastHover) { clearHover(); lastHover = hit.el; lastHover.classList.add('reparent-target'); }
        else if (!hit) { clearHover(); }
        Edges.render();
        Minimap.render();
        State.isDirty = true;
      });
      el.addEventListener('pointerup', (e: PointerEvent) => {
        if (!dragging) return;
        dragging = false;
        const hit = findSlotAt(e.clientX, e.clientY);
        el.classList.remove('dragging');
        el.releasePointerCapture(e.pointerId);
        clearHover();
        if (hit) {
          callbacks.reparentNode(node.nodeId, hit.parentId, hit.slot);
        } else {
          callbacks.markDirty();
        }
      });
    } else {
      el.addEventListener('pointerdown', (e: PointerEvent) => {
        const t = e.target as HTMLElement;
        if (t.classList.contains('port')) return;
        if (t.classList.contains('drop-zone')) return;
        e.stopPropagation();
        callbacks.select('node', node.nodeId);
      });
    }

    // Collapse button delegation
    el.querySelectorAll('[data-action="toggle-collapse"]').forEach(btn => {
      btn.addEventListener('click', (e) => {
        e.stopPropagation();
        callbacks.toggleCollapse(node.nodeId);
      });
    });

    this.attachOwnPortHandlers(el);
    this.attachOwnSlotHandlers(el, node);
    this.attachInsertionZones(el, node);
  },

  attachOwnPortHandlers(el: HTMLElement) {
    el.querySelectorAll(':scope > .port').forEach(port => {
      port.addEventListener('pointerdown', (e: Event) => {
        if (State.isLocked) return;
        e.stopPropagation();
        Edges.startDrag(port as HTMLElement);
      });
    });
    el.querySelectorAll(':scope > .branch-ports > .branch-port > .port').forEach(port => {
      port.addEventListener('pointerdown', (e: Event) => {
        if (State.isLocked) return;
        e.stopPropagation();
        Edges.startDrag(port as HTMLElement);
      });
    });
  },

  attachOwnSlotHandlers(el: HTMLElement, node: FlowNode) {
    const slots = getSlots(node);
    slots.forEach(slot => {
      const dropEl = node.runtimeType === 'container' && slot === '_body'
        ? el.querySelector(':scope > .container-body') as HTMLElement | null
        : el.querySelector(`:scope > .branch-ports > .branch-port[data-branch="${CSS.escape(slot)}"]`) as HTMLElement | null;
      if (!dropEl) return;
      dropEl.addEventListener('dragover', (e: DragEvent) => {
        if (!e.dataTransfer?.types.includes('text/flow-node')) return;
        e.preventDefault();
        e.stopPropagation();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
        dropEl.classList.add('drop-active');
      });
      dropEl.addEventListener('dragleave', (e: DragEvent) => {
        if (!dropEl.contains(e.relatedTarget as Node)) dropEl.classList.remove('drop-active');
      });
      dropEl.addEventListener('drop', (e: DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        dropEl.classList.remove('drop-active');
        const type = e.dataTransfer?.getData('text/flow-node');
        if (!type) return;
        callbacks.createChildNode(type, node.nodeId, slot);
      });
    });
  },

  attachInsertionZones(el: HTMLElement, node: FlowNode) {
    el.querySelectorAll(':scope > .drop-zone').forEach(dz => {
      const dropZone = dz as HTMLElement;
      dropZone.addEventListener('dragover', (e: DragEvent) => {
        if (!e.dataTransfer?.types.includes('text/flow-node')) return;
        e.preventDefault();
        e.stopPropagation();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
        dropZone.classList.add('drop-active');
      });
      dropZone.addEventListener('dragleave', () => dropZone.classList.remove('drop-active'));
      dropZone.addEventListener('drop', (e: DragEvent) => {
        e.preventDefault();
        e.stopPropagation();
        dropZone.classList.remove('drop-active');
        const type = e.dataTransfer?.getData('text/flow-node');
        if (!type) return;
        const position = dropZone.dataset.position || 'after';
        callbacks.insertNodeRelative(type, node.nodeId, position);
      });
    });
  }
};
