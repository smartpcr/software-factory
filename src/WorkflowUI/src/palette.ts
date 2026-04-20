// -----------------------------------------------------------------------
// Palette panel rendering
// -----------------------------------------------------------------------

import { NodeTypes, PaletteOrder } from './registry';

export const Palette = {
  /** Track collapsed categories */
  _collapsed: new Set<string>(),

  render() {
    const container = document.getElementById('paletteSections');
    if (!container) return;
    let html = '';
    PaletteOrder.forEach(section => {
      const collapsed = this._collapsed.has(section.category);
      html += `<div class="palette-section ${collapsed ? 'collapsed' : ''}">
        <div class="palette-section-title" data-category="${section.category}">
          <svg class="palette-chevron" width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 9l6 6 6-6"/></svg>
          <span>${section.category}</span>
          <span class="palette-section-count">${section.types.filter(t => NodeTypes[t]).length}</span>
        </div>
        <div class="palette-section-items">`;
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
      html += `</div></div>`;
    });
    container.innerHTML = html;

    // Collapse toggle
    container.querySelectorAll('.palette-section-title').forEach(title => {
      title.addEventListener('click', () => {
        const cat = (title as HTMLElement).dataset.category!;
        const section = title.closest('.palette-section') as HTMLElement;
        if (this._collapsed.has(cat)) {
          this._collapsed.delete(cat);
          section.classList.remove('collapsed');
        } else {
          this._collapsed.add(cat);
          section.classList.add('collapsed');
        }
      });
    });

    // Drag handlers
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
