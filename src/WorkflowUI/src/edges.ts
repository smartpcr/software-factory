// -----------------------------------------------------------------------
// Edge SVG rendering and connection drag
// -----------------------------------------------------------------------

import type { FlowEdge, AppCallbacks } from './types';
import { State } from './state';
import { getPortPos } from './utils';
import { Canvas } from './canvas';

let callbacks: AppCallbacks;

export const Edges = {
  init(cb: AppCallbacks) { callbacks = cb; },

  render() {
    const svg = document.getElementById('edgesSvg');
    if (!svg) return;
    let html = '<defs><marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="var(--stroke-3)"/></marker><marker id="arrow-err" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="var(--err)"/></marker><marker id="arrow-active" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z" fill="var(--run)"/></marker></defs>';

    State.edges.forEach((edge, id) => {
      const src = State.nodes.get(edge.sourceNodeId);
      const tgt = State.nodes.get(edge.targetNodeId);
      if (!src || !tgt) return;
      const sp = getPortPos(src, edge.sourcePort, edge.sourcePortName);
      const tp = getPortPos(tgt, 'in');
      const path = this.buildPath(sp, tp);
      const isErr = edge.sourcePort === 'error';
      const selected = State.selection.kind === 'edge' && State.selection.id === id;
      const active = edge._active;
      const cls = [
        'edge-path',
        isErr ? 'error-edge' : '',
        selected ? 'selected' : '',
        active ? 'active' : ''
      ].filter(Boolean).join(' ');

      const marker = isErr ? 'url(#arrow-err)' : active ? 'url(#arrow-active)' : 'url(#arrow)';
      html += `<path class="${cls}" d="${path}" data-edge-id="${id}" marker-end="${marker}"/>`;

      // Label: user-defined label takes priority, then branch port name
      const lbl = edge.label || (edge.sourcePort === 'branch' ? edge.sourcePortName : undefined);
      if (lbl) {
        const midX = (sp.x + tp.x) / 2;
        const midY = (sp.y + tp.y) / 2;
        const w = lbl.length * 6 + 10;
        html += `<rect class="edge-label-bg" x="${midX - w / 2}" y="${midY - 8}" width="${w}" height="16" rx="8" data-edge-id="${id}"/>`;
        html += `<text class="edge-label" x="${midX}" y="${midY}" text-anchor="middle" dominant-baseline="middle" data-edge-id="${id}">${lbl}</text>`;
      }

      if (active) {
        html += `<path class="edge-flow" d="${path}"/>`;
      }
    });

    svg.innerHTML = html;

    // Click to select
    svg.querySelectorAll('.edge-path').forEach(p => {
      p.addEventListener('click', (e: Event) => {
        e.stopPropagation();
        const pathEl = p as SVGPathElement;
        callbacks.select('edge', pathEl.dataset.edgeId || '');
      });
    });

    // Double-click to add/edit label
    svg.querySelectorAll('.edge-path').forEach(p => {
      p.addEventListener('dblclick', (e: Event) => {
        e.stopPropagation();
        const edgeId = (p as SVGPathElement).dataset.edgeId || '';
        this.showLabelEditor(edgeId, e as MouseEvent);
      });
    });
    // Also allow dblclick on existing label text/bg
    svg.querySelectorAll('.edge-label, .edge-label-bg').forEach(el => {
      el.addEventListener('dblclick', (e: Event) => {
        e.stopPropagation();
        const edgeId = (el as SVGElement).dataset.edgeId || '';
        this.showLabelEditor(edgeId, e as MouseEvent);
      });
    });
  },

  showLabelEditor(edgeId: string, evt: MouseEvent) {
    const edge = State.edges.get(edgeId);
    if (!edge) return;

    // Remove any existing editor
    document.querySelector('.edge-label-editor')?.remove();

    const editor = document.createElement('div');
    editor.className = 'edge-label-editor';
    editor.style.left = evt.clientX + 'px';
    editor.style.top = evt.clientY + 'px';

    const input = document.createElement('input');
    input.className = 'edge-label-input';
    input.value = edge.label || '';
    input.placeholder = 'Label…';
    editor.appendChild(input);
    document.body.appendChild(editor);

    input.focus();
    input.select();

    const commit = () => {
      const val = input.value.trim();
      edge.label = val || undefined;
      editor.remove();
      callbacks.markDirty();
      this.render();
    };

    input.addEventListener('keydown', (e: KeyboardEvent) => {
      if (e.key === 'Enter') { e.preventDefault(); commit(); }
      if (e.key === 'Escape') { editor.remove(); }
    });
    input.addEventListener('blur', commit);
  },

  buildPath(s: { x: number; y: number }, t: { x: number; y: number }): string {
    const dx = t.x - s.x, dy = t.y - s.y;
    const dist = Math.hypot(dx, dy) || 1;
    const back = Math.min(11, Math.max(0, dist - 2));
    const tx = t.x - dx * back / dist;
    const ty = t.y - dy * back / dist;
    const vdy = Math.abs(ty - s.y);
    const offset = Math.max(40, Math.min(vdy * 0.6, 120));
    return `M ${s.x} ${s.y} C ${s.x} ${s.y + offset}, ${tx} ${ty - offset}, ${tx} ${ty}`;
  },

  startDrag(port: HTMLElement) {
    const nodeId = port.dataset.nodeId || '';
    const portKind = port.dataset.portKind || '';
    const portName = port.dataset.portName;
    if (portKind === 'in') return;

    const node = State.nodes.get(nodeId);
    if (!node) return;
    const startPos = getPortPos(node,
      portKind === 'branch' ? 'branch' : portKind,
      portName);

    port.classList.add('connecting');

    const svg = document.getElementById('edgesSvg')!;
    const preview = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    preview.classList.add('drag-edge-preview');
    svg.appendChild(preview);

    const onMove = (e: PointerEvent) => {
      const pos = Canvas.screenToCanvas(e.clientX, e.clientY);
      const path = this.buildPath(startPos, pos);
      preview.setAttribute('d', path);
    };
    const onUp = (e: PointerEvent) => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      preview.remove();
      port.classList.remove('connecting');

      const target = document.elementFromPoint(e.clientX, e.clientY) as HTMLElement | null;
      if (target && target.classList.contains('port') && target.dataset.portKind === 'in') {
        const targetId = target.dataset.nodeId;
        if (targetId && targetId !== nodeId) {
          callbacks.createEdge({
            sourceNodeId: nodeId,
            sourcePort: portKind,
            sourcePortName: portName,
            targetNodeId: targetId,
            targetPort: 'in'
          });
        }
      }
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  },

  getSuccessors(nodeId: string, portKind: string, portName?: string): FlowEdge[] {
    const results: FlowEdge[] = [];
    State.edges.forEach(edge => {
      if (edge.sourceNodeId !== nodeId) return;
      if (portKind && edge.sourcePort !== portKind) return;
      if (portName !== undefined && edge.sourcePortName !== portName) return;
      results.push(edge);
    });
    return results;
  }
};
