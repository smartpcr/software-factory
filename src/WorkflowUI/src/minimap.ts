// -----------------------------------------------------------------------
// Minimap SVG rendering + drag navigation
// -----------------------------------------------------------------------

import { State } from './state';
import { NodeTypes } from './registry';
import { Canvas } from './canvas';

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

    const nodes = [...State.nodes.values()].filter(n => !n.parentId);
    if (!nodes.length) { svg.innerHTML = ''; return; }
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    nodes.forEach(n => {
      minX = Math.min(minX, n.x);
      minY = Math.min(minY, n.y);
      maxX = Math.max(maxX, n.x + 220);
      maxY = Math.max(maxY, n.y + 100);
    });
    const pad = 40;
    minX -= pad; minY -= pad; maxX += pad; maxY += pad;
    const w = maxX - minX, h = maxY - minY;

    // Cache for coordinate conversion
    vbMinX = minX; vbMinY = minY; vbW = w; vbH = h;

    svg.setAttribute('viewBox', `${minX} ${minY} ${w} ${h}`);

    let html = '';
    nodes.forEach(n => {
      const c = NodeTypes[n.runtimeType]?.color || 'var(--text-3)';
      html += `<rect x="${n.x}" y="${n.y}" width="220" height="80" rx="8" fill="${c}" opacity="0.5"/>`;
    });

    const canvasRect = (Canvas.el || document.getElementById('canvas'));
    if (canvasRect) {
      const rect = canvasRect.getBoundingClientRect();
      const vx = -State.canvas.x / State.canvas.zoom;
      const vy = -State.canvas.y / State.canvas.zoom;
      const vw = rect.width / State.canvas.zoom;
      const vh = rect.height / State.canvas.zoom;
      html += `<rect class="minimap-viewport" x="${vx}" y="${vy}" width="${vw}" height="${vh}" fill="rgba(229,179,106,0.08)" stroke="#d4a657" stroke-width="${3 * vbW / 180}" stroke-dasharray="${6 * vbW / 180} ${4 * vbW / 180}"/>`;
    }

    svg.innerHTML = html;
  }
};
