// -----------------------------------------------------------------------
// Palette panel rendering
// -----------------------------------------------------------------------

import { NodeTypes, PaletteOrder } from './registry';

export const Palette = {
  render() {
    const container = document.getElementById('paletteSections');
    if (!container) return;
    let html = '';
    PaletteOrder.forEach(section => {
      html += `<div class="palette-section">
        <div class="palette-section-title">${section.category}</div>`;
      section.types.forEach(type => {
        const t = NodeTypes[type];
        if (!t) return;
        html += `
          <div class="palette-item" draggable="true" data-type="${type}" style="color: ${t.color}">
            <div class="palette-item-icon">${t.icon}</div>
            <div class="palette-item-text">
              <div class="palette-item-name">${t.label}</div>
              <div class="palette-item-desc">${t.desc}</div>
            </div>
          </div>
        `;
      });
      html += `</div>`;
    });
    container.innerHTML = html;

    container.querySelectorAll('.palette-item').forEach(item => {
      const el = item as HTMLElement;
      el.addEventListener('dragstart', (e: DragEvent) => {
        e.dataTransfer?.setData('text/flow-node', el.dataset.type || '');
        if (e.dataTransfer) e.dataTransfer.effectAllowed = 'copy';
        document.body.classList.add('dragging-palette');
      });
      el.addEventListener('dragend', () => {
        document.body.classList.remove('dragging-palette');
        document.querySelectorAll('.drop-active').forEach(d => d.classList.remove('drop-active'));
      });
    });
  },

  filter(q: string) {
    const query = q.toLowerCase().trim();
    document.querySelectorAll('.palette-item').forEach(item => {
      const el = item as HTMLElement;
      const name = el.querySelector('.palette-item-name')?.textContent?.toLowerCase() || '';
      const desc = el.querySelector('.palette-item-desc')?.textContent?.toLowerCase() || '';
      const match = !query || name.includes(query) || desc.includes(query);
      el.style.display = match ? '' : 'none';
    });
    document.querySelectorAll('.palette-section').forEach(sec => {
      const el = sec as HTMLElement;
      const visible = [...el.querySelectorAll('.palette-item')].some(i => (i as HTMLElement).style.display !== 'none');
      el.style.display = visible ? '' : 'none';
    });
  }
};
