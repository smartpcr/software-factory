// -----------------------------------------------------------------------
// Minimap SVG rendering
// -----------------------------------------------------------------------

import { State } from './state';
import { NodeTypes } from './registry';
import { Canvas } from './canvas';

export const Minimap = {
  render() {
    const svg = document.getElementById('minimapSvg');
    if (!svg) return;
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
    svg.setAttribute('viewBox', `${minX} ${minY} ${w} ${h}`);

    let html = '';
    nodes.forEach(n => {
      const c = NodeTypes[n.type]?.color || 'var(--text-3)';
      html += `<rect x="${n.x}" y="${n.y}" width="220" height="80" rx="8" fill="${c}" opacity="0.5"/>`;
    });

    if (Canvas.el) {
      const rect = Canvas.el.getBoundingClientRect();
      const vx = -State.canvas.x / State.canvas.zoom;
      const vy = -State.canvas.y / State.canvas.zoom;
      const vw = rect.width / State.canvas.zoom;
      const vh = rect.height / State.canvas.zoom;
      html += `<rect x="${vx}" y="${vy}" width="${vw}" height="${vh}" fill="none" stroke="#d4a657" stroke-width="3" stroke-dasharray="6 4"/>`;
    }

    svg.innerHTML = html;
  }
};
