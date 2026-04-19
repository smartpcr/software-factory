// -----------------------------------------------------------------------
// Utility functions
// -----------------------------------------------------------------------

import type { FlowNode, Point } from './types';
import { NodeTypes } from './registry';
import { State } from './state';

export function uid(prefix = 'n'): string {
  return prefix + '_' + Math.random().toString(36).slice(2, 9);
}

export function now(): number {
  return Date.now();
}

export function clone<T>(v: T): T {
  return JSON.parse(JSON.stringify(v));
}

export function escapeHtml(s: string): string {
  const div = document.createElement('div');
  div.textContent = s;
  return div.innerHTML;
}

/** Slot names for a node (branch names for if/switch/parallel/foreach, '_body' for container). */
export function getSlots(node: FlowNode | undefined): string[] {
  if (!node) return [];
  const t = NodeTypes[node.type];
  if (t && t.branches) return node.branches || t.branches;
  if (node.type === 'container') return ['_body'];
  return [];
}

export function ensureChildren(node: FlowNode): void {
  const slots = getSlots(node);
  if (!slots.length) return;
  if (!node.children) node.children = {};
  slots.forEach(s => {
    if (!node.children![s]) node.children![s] = [];
  });
}

export function toast(msg: string, kind = 'info', ms = 2800): void {
  const el = document.createElement('div');
  el.className = 'toast ' + kind;
  const span = document.createElement('span');
  span.textContent = msg;
  el.appendChild(span);
  document.getElementById('toasts')?.appendChild(el);
  setTimeout(() => { el.style.opacity = '0'; el.style.transform = 'translateX(20px)'; }, ms - 300);
  setTimeout(() => el.remove(), ms);
}

/**
 * Port positions in canvasInner coordinate space.
 * Prefers live DOM measurement, falls back to math.
 */
export function getPortPos(node: FlowNode, portKind: string, portName?: string): Point {
  const dom = getPortPosFromDom(node.id, portKind, portName);
  if (dom) return dom;

  const width = node.width || 220;
  const headH = 38;
  const bodyH = 32;
  const branchH = 48;
  const type = NodeTypes[node.type];

  let height = headH + bodyH;
  if (type?.branches && type.branches.length) height += branchH;
  if (node.type === 'container') height = Math.max(160, node.height || 160);
  node._cachedHeight = height;

  if (portKind === 'in') return { x: node.x + width / 2, y: node.y };
  if (portKind === 'out') return { x: node.x + width / 2, y: node.y + height };
  if (portKind === 'error') return { x: node.x + width, y: node.y + headH + bodyH / 2 };
  if (portKind === 'branch') {
    const branches = type?.branches || [];
    const idx = branches.indexOf(portName || '');
    if (idx < 0) return { x: node.x + width / 2, y: node.y + height };
    const bw = (width - 20 - (branches.length - 1) * 8) / branches.length;
    const bx = 10 + idx * (bw + 8) + bw / 2;
    return { x: node.x + bx, y: node.y + height };
  }
  return { x: node.x, y: node.y };
}

function getPortPosFromDom(nodeId: string, portKind: string, portName?: string): Point | null {
  const inner = document.getElementById('canvasInner');
  if (!inner) return null;
  let portEl: Element | null = null;
  if (portKind === 'branch') {
    portEl = document.querySelector(
      `.node[data-id="${nodeId}"] > .branch-ports > .branch-port[data-branch="${CSS.escape(portName || '')}"] > .port`
    );
  } else {
    portEl = document.querySelector(`.node[data-id="${nodeId}"] > .port.${portKind}`);
  }
  if (!portEl || !portEl.isConnected) return null;
  const innerRect = inner.getBoundingClientRect();
  const portRect = portEl.getBoundingClientRect();
  const zoom = State.canvas.zoom || 1;
  return {
    x: (portRect.left + portRect.width / 2 - innerRect.left) / zoom,
    y: (portRect.top + portRect.height / 2 - innerRect.top) / zoom,
  };
}
