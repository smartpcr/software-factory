// -----------------------------------------------------------------------
// Canvas pan/zoom/drag-drop
// -----------------------------------------------------------------------

import { State } from './state';
import type { Point, AppCallbacks } from './types';

let appCallbacks: AppCallbacks;
let canvasEl: HTMLElement;
let innerEl: HTMLElement;

export const Canvas = {
  el: null as HTMLElement | null,
  inner: null as HTMLElement | null,

  init(callbacks: AppCallbacks) {
    appCallbacks = callbacks;
    canvasEl = document.getElementById('canvas')!;
    innerEl = document.getElementById('canvasInner')!;
    this.el = canvasEl;
    this.inner = innerEl;

    let panning = false;
    let panStart = { x: 0, y: 0 };

    canvasEl.addEventListener('pointerdown', (e: PointerEvent) => {
      const target = e.target as HTMLElement;
      if (target === canvasEl || target.classList.contains('canvas-grid') || target.id === 'canvasInner') {
        panning = true;
        panStart = { x: e.clientX - State.canvas.x, y: e.clientY - State.canvas.y };
        canvasEl.classList.add('panning');
        appCallbacks.select(null);
      }
    });
    window.addEventListener('pointermove', (e: PointerEvent) => {
      if (panning) {
        State.canvas.x = e.clientX - panStart.x;
        State.canvas.y = e.clientY - panStart.y;
        this.apply();
      }
    });
    window.addEventListener('pointerup', () => {
      panning = false;
      canvasEl.classList.remove('panning');
    });

    canvasEl.addEventListener('wheel', (e: WheelEvent) => {
      e.preventDefault();
      const delta = -e.deltaY;
      const factor = delta > 0 ? 1.08 : 0.93;
      this.zoomAtPoint(factor, e.clientX, e.clientY);
    }, { passive: false });

    canvasEl.addEventListener('dragover', (e: DragEvent) => {
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
    });
    canvasEl.addEventListener('drop', (e: DragEvent) => {
      e.preventDefault();
      const type = e.dataTransfer?.getData('text/flow-node');
      if (!type) return;
      const pos = this.screenToCanvas(e.clientX, e.clientY);
      appCallbacks.createNode(type, pos.x - 110, pos.y - 20);
    });
  },

  apply() {
    if (!innerEl) return;
    innerEl.style.transform = `translate(${State.canvas.x}px, ${State.canvas.y}px) scale(${State.canvas.zoom})`;
    const zoomEl = document.getElementById('zoomLevel');
    if (zoomEl) zoomEl.textContent = Math.round(State.canvas.zoom * 100) + '%';
  },

  zoom(factor: number) {
    const rect = canvasEl.getBoundingClientRect();
    this.zoomAtPoint(factor, rect.left + rect.width / 2, rect.top + rect.height / 2);
  },

  zoomAtPoint(factor: number, cx: number, cy: number) {
    const newZoom = Math.max(0.2, Math.min(2.5, State.canvas.zoom * factor));
    const rect = canvasEl.getBoundingClientRect();
    const mx = cx - rect.left;
    const my = cy - rect.top;
    State.canvas.x = mx - (mx - State.canvas.x) * (newZoom / State.canvas.zoom);
    State.canvas.y = my - (my - State.canvas.y) * (newZoom / State.canvas.zoom);
    State.canvas.zoom = newZoom;
    this.apply();
  },

  reset() {
    State.canvas = { x: 200, y: 120, zoom: 1 };
    this.apply();
  },

  fitToView() {
    const nodes = [...State.nodes.values()];
    if (!nodes.length) { this.reset(); return; }
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    nodes.forEach(n => {
      minX = Math.min(minX, n.x);
      minY = Math.min(minY, n.y);
      maxX = Math.max(maxX, n.x + (n.width || 220));
      maxY = Math.max(maxY, n.y + (n._cachedHeight || 120));
    });
    const w = maxX - minX + 200;
    const h = maxY - minY + 200;
    const rect = canvasEl.getBoundingClientRect();
    const zoom = Math.min(rect.width / w, rect.height / h, 1.2);
    State.canvas.zoom = zoom;
    State.canvas.x = -minX * zoom + (rect.width - w * zoom) / 2 + 100 * zoom;
    State.canvas.y = -minY * zoom + (rect.height - h * zoom) / 2 + 100 * zoom;
    this.apply();
  },

  screenToCanvas(x: number, y: number): Point {
    const rect = canvasEl.getBoundingClientRect();
    return {
      x: (x - rect.left - State.canvas.x) / State.canvas.zoom,
      y: (y - rect.top - State.canvas.y) / State.canvas.zoom
    };
  }
};
