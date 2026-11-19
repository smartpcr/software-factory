import { useEffect, useRef, useState } from 'react';
import { useConfirmStore } from '@/state/useConfirmStore';

/**
 * Global terminal-styled confirm dialog. Rendered once at the AppShell so
 * any view can call `useConfirmStore.getState().ask({...})` and receive a
 * Promise<boolean>. Replaces `window.confirm` everywhere so the dialog
 * matches the rest of the surface (mono font, amber/red accents on dim
 * slate, hairline panel borders, glyph icon). Esc cancels, Enter confirms,
 * the primary button is autofocused so a single Enter clears the dialog
 * for keyboard-driven operators.
 */
export function ConfirmDialog() {
  const pending = useConfirmStore((s) => s.pending);
  const cancel = useConfirmStore((s) => s.cancel);
  const confirm = useConfirmStore((s) => s.confirm);
  const confirmBtnRef = useRef<HTMLButtonElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | HTMLInputElement | null>(null);
  const [value, setValue] = useState('');

  // askText: seed the field each time a new dialog opens.
  useEffect(() => {
    if (pending?.input) setValue(pending.input.initialValue ?? '');
  }, [pending]);

  // Esc cancels — always. Enter is handled NATIVELY by whatever element
  // has focus (the autofocused confirm button, or the cancel button if the
  // user tabbed). The earlier global `Enter → confirm()` was preventing
  // Cancel's native button activation: when focus moved to Cancel and the
  // user pressed Enter, this handler fired confirm() first and called
  // e.preventDefault(), so the Cancel button's onClick never ran. Trusting
  // native button behavior is the simpler + correct fix.
  useEffect(() => {
    if (!pending) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') { e.preventDefault(); cancel(); }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [pending, cancel]);

  // Autofocus the text field for askText, else the primary button so Enter
  // dismisses without extra clicks.
  useEffect(() => {
    if (!pending) return;
    if (pending.input) inputRef.current?.focus();
    else confirmBtnRef.current?.focus();
  }, [pending]);

  if (!pending) return null;

  const tone = pending.tone ?? 'warn';
  const confirmLabel = pending.confirmLabel ?? 'Confirm';
  const cancelLabel  = pending.cancelLabel ?? 'Cancel';
  const input = pending.input;
  const blocked = !!input && (input.required ?? true) && value.trim().length === 0;
  const accept = () => { if (!blocked) confirm(input ? value : undefined); };

  // Allow plain strings to carry paragraph breaks via "\n\n".
  const bodyNode = typeof pending.body === 'string'
    ? pending.body.split('\n\n').map((p, i) => (
        <p key={i} className="confirm-dialog__body-para">{p}</p>
      ))
    : pending.body;

  return (
    <div
      className="confirm-dialog-root"
      role="dialog"
      aria-modal="true"
      aria-labelledby="confirm-dialog-title"
    >
      <div className="confirm-dialog__backdrop" onClick={cancel} />
      <div className={`confirm-dialog confirm-dialog--${tone}`}>
        <div className="confirm-dialog__hd">
          <span className={`confirm-dialog__icon confirm-dialog__icon--${tone}`} aria-hidden>
            {tone === 'danger' ? '✕' : tone === 'info' ? 'ⓘ' : '!'}
          </span>
          <span className="confirm-dialog__eyebrow">
            {input ? 'INPUT' : tone === 'danger' ? 'DESTRUCTIVE' : tone === 'info' ? 'CONFIRM' : 'WARNING'}
          </span>
        </div>
        <h3 className="confirm-dialog__title" id="confirm-dialog-title">{pending.title}</h3>
        {bodyNode && <div className="confirm-dialog__body">{bodyNode}</div>}
        {pending.checklist && pending.checklist.length > 0 && (
          <ul className="confirm-dialog__checklist">
            {pending.checklist.map((item, i) => (
              <li key={i}><span className="confirm-dialog__bullet">›</span>{item}</li>
            ))}
          </ul>
        )}
        {input && (
          <label className="confirm-dialog__field">
            {input.label && <span className="confirm-dialog__field-label">{input.label}</span>}
            {(input.rows ?? 1) > 1 ? (
              <textarea
                ref={(el) => { inputRef.current = el; }}
                className="confirm-dialog__input"
                rows={input.rows}
                value={value}
                placeholder={input.placeholder}
                maxLength={input.maxLength}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => {
                  // Enter inserts a newline; Ctrl/Cmd+Enter submits.
                  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); accept(); }
                }}
              />
            ) : (
              <input
                ref={(el) => { inputRef.current = el; }}
                className="confirm-dialog__input"
                value={value}
                placeholder={input.placeholder}
                maxLength={input.maxLength}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); accept(); } }}
              />
            )}
          </label>
        )}
        <div className="confirm-dialog__actions">
          <button
            type="button"
            className="confirm-dialog__btn confirm-dialog__btn--cancel"
            onClick={cancel}
          >
            {cancelLabel}
          </button>
          <button
            ref={confirmBtnRef}
            type="button"
            className={`confirm-dialog__btn confirm-dialog__btn--confirm confirm-dialog__btn--${tone}`}
            onClick={accept}
            disabled={blocked}
          >
            {confirmLabel}
          </button>
        </div>
        <div className="confirm-dialog__hint">
          {input && (input.rows ?? 1) > 1
            ? <><kbd>Esc</kbd> cancel · <kbd>Ctrl</kbd>+<kbd>Enter</kbd> {confirmLabel.toLowerCase()}</>
            : <><kbd>Esc</kbd> cancel · <kbd>Tab</kbd> to focus, <kbd>Enter</kbd> to activate</>}
        </div>
      </div>
    </div>
  );
}
