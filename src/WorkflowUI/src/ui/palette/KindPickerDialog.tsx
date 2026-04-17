import React, { useState, useCallback, useEffect, useRef } from 'react';
import { getRegistryEntries, type PaletteEntry } from '../nodes/nodeKindRegistry';
import { filterPaletteEntries } from './filterPaletteEntries';
import type { RuntimeType } from '../../engine-contract';

export interface KindPickerDialogProps {
  open: boolean;
  onSelect: (runtimeType: RuntimeType) => void;
  onClose: () => void;
}

export function KindPickerDialog({ open, onSelect, onClose }: KindPickerDialogProps) {
  const [query, setQuery] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);
  const allEntries = getRegistryEntries();
  const filtered = filterPaletteEntries(allEntries, query);

  useEffect(() => {
    if (open) {
      setQuery('');
      inputRef.current?.focus();
    }
  }, [open]);

  const handleSelect = useCallback(
    (entry: PaletteEntry) => {
      onSelect(entry.runtimeType);
      onClose();
    },
    [onSelect, onClose],
  );

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    },
    [onClose],
  );

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-label="Create node"
      data-testid="kind-picker-dialog"
      onKeyDown={handleKeyDown}
      style={{
        position: 'fixed',
        top: '50%',
        left: '50%',
        transform: 'translate(-50%, -50%)',
        background: '#fff',
        border: '1px solid #d1d5db',
        borderRadius: 8,
        padding: 16,
        zIndex: 1000,
        minWidth: 280,
        maxHeight: 400,
        overflow: 'auto',
        boxShadow: '0 4px 6px rgba(0,0,0,0.1)',
      }}
    >
      <input
        ref={inputRef}
        type="search"
        placeholder="Search node type..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        data-testid="kind-picker-search"
        style={{ width: '100%', marginBottom: 8, padding: '4px 8px' }}
      />
      {filtered.map((entry) => (
        <button
          key={entry.runtimeType}
          type="button"
          onClick={() => handleSelect(entry)}
          data-testid={`kind-picker-item-${entry.runtimeType}`}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 8,
            width: '100%',
            padding: '6px 8px',
            border: 'none',
            background: 'transparent',
            cursor: 'pointer',
            fontSize: 13,
            textAlign: 'left',
            borderRadius: 4,
          }}
        >
          <span aria-hidden="true">{entry.icon.emoji}</span>
          <span>{entry.icon.label}</span>
          <span style={{ color: '#9ca3af', fontSize: 11 }}>{entry.description}</span>
        </button>
      ))}
    </div>
  );
}