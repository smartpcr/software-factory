import React, { useState, useCallback } from 'react';
import { buildPaletteCatalog } from './paletteCatalog';
import { filterPaletteEntries } from './filterPaletteEntries';
import { DRAG_TYPE, encodeDragPayload } from './dragPayload';
import type { PaletteEntry } from '../nodes/nodeKindRegistry';
import './Palette.css';

export function Palette() {
  const [query, setQuery] = useState('');
  const catalog = buildPaletteCatalog();

  const handleDragStart = useCallback(
    (entry: PaletteEntry, e: React.DragEvent) => {
      e.dataTransfer.setData(DRAG_TYPE, encodeDragPayload({ runtimeType: entry.runtimeType }));
      e.dataTransfer.effectAllowed = 'move';
      document.body.classList.add('dragging-palette');
    },
    [],
  );

  const handleDragEnd = useCallback(() => {
    document.body.classList.remove('dragging-palette');
    document.querySelectorAll('.drop-active').forEach((el) => el.classList.remove('drop-active'));
  }, []);

  return (
    <div className="palette" data-testid="palette" aria-label="Node palette">
      <div className="palette-search">
        <input
          type="search"
          placeholder="Search components…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          data-testid="palette-search"
          aria-label="Filter palette"
        />
      </div>
      {catalog.map((group) => {
        const filtered = filterPaletteEntries(group.entries, query);
        if (filtered.length === 0) return null;
        return (
          <div key={group.group} className="palette-section" data-testid={`palette-group-${group.group}`}>
            <div className="palette-section-title">{group.label}</div>
            {filtered.map((entry) => {
              const IconComp = entry.icon.Icon;
              return (
                <div
                  key={entry.runtimeType}
                  className="palette-item"
                  draggable
                  onDragStart={(e) => handleDragStart(entry, e)}
                  onDragEnd={handleDragEnd}
                  data-testid={`palette-item-${entry.runtimeType}`}
                  role="button"
                  tabIndex={0}
                  aria-label={`Drag ${entry.icon.label} node`}
                  style={{ color: entry.icon.cssColor }}
                >
                  <div className="palette-item-icon">
                    <IconComp size={14} />
                  </div>
                  <div className="palette-item-text">
                    <div className="palette-item-name">{entry.icon.label}</div>
                    <div className="palette-item-desc">{entry.description}</div>
                  </div>
                </div>
              );
            })}
          </div>
        );
      })}
    </div>
  );
}