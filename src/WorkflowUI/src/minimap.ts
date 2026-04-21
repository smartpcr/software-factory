// -----------------------------------------------------------------------
// Minimap SVG rendering + drag navigation
// -----------------------------------------------------------------------

import { State } from './state';
import { NodeTypes } from './registry';
import { Canvas } from './canvas';
import { getPortPos } from './utils';

// Cached viewBox bounds for coordinate conversion
let vbMinX = 0, vbMinY = 0, vbW = 1, vbH = 1;

function screenToMinimap(clientX: number, clientY: number): { x: number; y: number } {
  const svg = document.getElementById('minimapSvg');
  if (!svg) return { x: 0, y: 0 };
  const rect = svg.getBoundingClientRect();
  const rx = (clientX - rect.left) / rect.width;
  const ry = (clientY - rect.top) / rect.height;
  return {
    x: vbMinX + rx * vbW,
    y: vbMinY + ry * vbH,
  };
}

function panCanvasTo(cx: number, cy: number) {
  if (!Canvas.el) return;
  const rect = Canvas.el.getBoundingClientRect();
  const vw = rect.width / State.canvas.zoom;
  const vh = rect.height / State.canvas.zoom;
  State.canvas.x = -(cx - vw / 2) * State.canvas.zoom;
  State.canvas.y = -(cy - vh / 2) * State.canvas.zoom;
  Canvas.apply();
}

export const Minimap = {
  _bound: false,

  init() {
    if (this._bound) return;
    this._bound = true;
    const container = document.getElementById('minimap');
    if (!container) return;

    let dragging = false;

    container.addEventListener('pointerdown', (e: PointerEvent) => {
      e.preventDefault();
      e.stopPropagation();
      dragging = true;
      container.setPointerCapture(e.pointerId);
      const pos = screenToMinimap(e.clientX, e.clientY);
      panCanvasTo(pos.x, pos.y);
    });

    container.addEventListener('pointermove', (e: PointerEvent) => {
      if (!dragging) return;
      const pos = screenToMinimap(e.clientX, e.clientY);
      panCanvasTo(pos.x, pos.y);
    });

    container.addEventListener('pointerup', (e: PointerEvent) => {
      if (!dragging) return;
      dragging = false;
      container.releasePointerCapture(e.pointerId);
    });
  },

  render() {
    const svg = document.getElementById('minimapSvg');
    if (!svg) return;

    // Lazy-bind drag handlers on first render
    if (!this._bound) this.init();

    const allNodes = [...State.nodes.values()];
    // Use DOM positions for child nodes (they're rendered inside parents)
    const nodeRects = new Map<string, { x: number; y: number; w: number; h: number }>();
    for (const n of allNodes) {
      const el = document.querySelector(`.node[data-id="${n.nodeId}"]`) as HTMLElement;
      if (el) {
        const layer = document.getElementById('nodesLayer');
        if (layer) {
          const lr = layer.getBoundingClientRect();
          const nr = el.getBoundingClientRect();
          const z = State.canvas.zoom;
          nodeRects.set(n.nodeId, {
            x: (nr.left - lr.left) / z,
            y: (nr.top - lr.top) / z,
            w: nr.width / z,
            h: nr.height / z,
          });
        }
      } else if (!n.parentId) {
        nodeRects.set(n.nodeId, { x: n.x, y: n.y, w: n.width || 220, h: 80 });
      }
    }

    if (!nodeRects.size) { svg.innerHTML = ''; return; }

    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const r of nodeRects.values()) {
      minX = Math.min(minX, r.x);
      minY = Math.min(minY, r.y);
      maxX = Math.max(maxX, r.x + r.w);
      maxY = Math.max(maxY, r.y + r.h);
    }
    const pad = 40;
    minX -= pad; minY -= pad; maxX += pad; maxY += pad;
    const w = maxX - minX, h = maxY - minY;
    vbMinX = minX; vbMinY = minY; vbW = w; vbH = h;
    svg.setAttribute('viewBox', `${minX} ${minY} ${w} ${h}`);

    const sw = Math.max(1, w / 180); // stroke scaling
    let html = '';

    // Draw edges
    State.edges.forEach(edge => {
      if (edge._internal) return;
      const sr = nodeRects.get(edge.sourceNodeId);
      const tr = nodeRects.get(edge.targetNodeId);
      if (!sr || !tr) return;
      const sx = sr.x + sr.w / 2, sy = sr.y + sr.h;
      const tx = tr.x + tr.w / 2, ty = tr.y;
      const isErr = edge.sourcePort === 'error';
      const stroke = isErr ? 'var(--err)' : 'var(--stroke-3)';
      const dash = isErr ? `stroke-dasharray="${2 * sw} ${2 * sw}"` : '';
      html += `<line x1="${sx}" y1="${sy}" x2="${tx}" y2="${ty}" stroke="${stroke}" stroke-width="${sw}" opacity="0.5" ${dash}/>`;
    });

    // Draw nodes
    for (const [nid, r] of nodeRects) {
      const n = State.nodes.get(nid);
      if (!n) continue;
      const c = NodeTypes[n.runtimeType]?.color || 'var(--text-3)';
      const isChild = !!n.parentId;
      html += `<rect x="${r.x}" y="${r.y}" width="${r.w}" height="${r.h}" rx="${isChild ? 4 : 8}" fill="${c}" opacity="${isChild ? 0.35 : 0.5}"/>`;
    }

    // Draw viewport rectangle
    const canvasRect = (Canvas.el || document.getElementById('canvas'));
    if (canvasRect) {
      const rect = canvasRect.getBoundingClientRect();
      const vx = -State.canvas.x / State.canvas.zoom;
      const vy = -State.canvas.y / State.canvas.zoom;
      const vw = rect.width / State.canvas.zoom;
      const vh = rect.height / State.canvas.zoom;
      html += `<rect class="minimap-viewport" x="${vx}" y="${vy}" width="${vw}" height="${vh}" fill="rgba(229,179,106,0.08)" stroke="#d4a657" stroke-width="${3 * sw}" stroke-dasharray="${6 * sw} ${4 * sw}"/>`;
    }

    svg.innerHTML = html;
  }
};
