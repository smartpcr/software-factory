import { useBoardStore } from '@/state/useBoardStore';
import { checkDragPolicy } from './policy';

// Verbatim port of the prototype's confirmation modal (lines 2942-2960).
// The body text comes from the policy result; we render it as HTML because
// the prototype's text contains <strong> tags.
export function ConfirmModal() {
  const pending = useBoardStore((s) => s.pendingMove);
  const cards = useBoardStore((s) => s.cards);
  const confirm = useBoardStore((s) => s.confirmPendingMove);
  const cancel = useBoardStore((s) => s.cancelPendingMove);

  const card = pending ? cards.find((c) => c.id === pending.cardId) : null;
  const policy = card && pending ? checkDragPolicy(card.col, pending.toCol, card) : null;
  const open = !!pending && !!policy?.confirm;

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        id="modal-backdrop"
        onClick={cancel}
      />
      <div className={`modal${open ? ' show' : ''}`} id="modal" role="dialog" aria-modal="true">
        <div className="modal-icon">
          <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
            <path d="M9 2 L 16 14 H 2 Z" stroke="currentColor" strokeWidth="1.5" fill="none" strokeLinejoin="round" />
            <line x1="9" y1="7" x2="9" y2="10.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            <circle cx="9" cy="12.5" r="0.8" fill="currentColor" />
          </svg>
        </div>
        <h3 className="modal-title" id="modal-title">{policy?.title ?? 'Confirm move'}</h3>
        <p
          className="modal-text"
          id="modal-text"
          dangerouslySetInnerHTML={{ __html: policy?.text ?? '' }}
        />
        <div className="modal-actions">
          <button className="btn btn-secondary" id="modal-cancel" onClick={cancel}>Cancel</button>
          <button className="btn btn-primary" id="modal-confirm" onClick={() => void confirm()}>
            Override & continue
          </button>
        </div>
      </div>
    </>
  );
}
