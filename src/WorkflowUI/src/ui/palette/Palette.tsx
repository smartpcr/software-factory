import React, { useState, useCallback } from 'react';
import { buildPaletteCatalog } from './paletteCatalog';
import { filterPaletteEntries } from './filterPaletteEntries';
import { DRAG_TYPE, encodeDragPayload } from './dragPayload';
import type { PaletteEntry } from '../nodes/nodeKindRegistry';

export function Palette() {
  const [query, setQuery] = useState('');
  const catalog = buildPaletteCatalog();

  const handleDragStart = useCallback(
    (entry: PaletteEntry, e: React.DragEvent) => {
      e.dataTransfer.setData(DRAG_TYPE, encodeDragPayload({ runtimeType: entry.runtimeType }));
      e.dataTransfer.effectAllowed = 'move';
    },
    [],
  );

  return (
    <aside data-testid="palette" aria-label="Node palette" style={{ padding: 8, width: 220 }}>
      <input
        type="search"
        placeholder="Search nodes..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        data-testid="palette-search"
        aria-label="Filter palette"
        style={{ width: '100%', marginBottom: 8, padding: '4px 8px' }}
      />
      {catalog.map((group) => {
        const filtered = filterPaletteEntries(group.entries, query);
        if (filtered.length === 0) return null;
        return (
          <div key={group.group} data-testid={`palette-group-${group.group}`}>
            <div style={{ fontWeight: 600, fontSize: 12, marginBottom: 4, color: '#6b7280' }}>
              {group.label}
            </div>
            {filtered.map((entry) => (
              <div
                key={entry.runtimeType}
                draggable
                onDragStart={(e) => handleDragStart(entry, e)}
                data-testid={`palette-item-${entry.runtimeType}`}
                role="button"
                tabIndex={0}
                aria-label={`Drag ${entry.icon.label} node`}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 6,
                  padding: '4px 6px',
                  cursor: 'grab',
                  borderRadius: 4,
                  fontSize: 13,
                }}
              >
                <span aria-hidden="true">{entry.icon.emoji}</span>
                <span>{entry.icon.label}</span>
              </div>
            ))}
          </div>
        );
      })}
    </aside>
  );
}