import { useState } from 'react';
import { useHotkeys } from 'react-hotkeys-hook';

// Phase 7.2 — keyboard reference. Opened with `?`. Closes on Esc or backdrop.
const SHORTCUTS: Array<{ keys: string[]; label: string }> = [
  { keys: ['?'],            label: 'Show this map' },
  { keys: ['Cmd/Ctrl', 'K'], label: 'Command palette' },
  { keys: ['Esc'],          label: 'Close panel / modal' },
  { keys: ['n'],            label: 'New card (on /plan)' },
  { keys: ['/'],            label: 'Focus search bar' },
  { keys: ['1'], label: 'Focus Backlog column' },
  { keys: ['2'], label: 'Focus Design column' },
  { keys: ['3'], label: 'Focus Build column' },
  { keys: ['4'], label: 'Focus Review column' },
  { keys: ['5'], label: 'Focus Done column' },
];

export function KeyboardMap() {
  const [open, setOpen] = useState(false);
  // react-hotkeys-hook fires both bindings reliably across keyboard layouts.
  useHotkeys('shift+/, ?', (e) => { e.preventDefault(); setOpen((v) => !v); }, { enableOnFormTags: false });
  useHotkeys('escape', () => setOpen(false), { enabled: open, enableOnFormTags: true });

  if (!open) return null;
  return (
    <>
      <div className="modal-backdrop show" onClick={() => setOpen(false)} />
      <div className="modal show keymap-modal" role="dialog" aria-modal="true">
        <h3 className="modal-title">Keyboard shortcuts</h3>
        <div className="modal-text">
          Use <kbd>?</kbd> any time to bring this map back.
        </div>
        <div className="keymap-grid">
          {SHORTCUTS.map((s) => (
            <div className="keymap-row" key={s.label}>
              <span>{s.label}</span>
              <span className="keymap-keys">
                {s.keys.map((k) => <kbd key={k}>{k}</kbd>)}
              </span>
            </div>
          ))}
        </div>
        <div className="modal-actions" style={{ marginTop: 18 }}>
          <button className="btn btn-secondary" onClick={() => setOpen(false)}>Close</button>
        </div>
      </div>
    </>
  );
}
