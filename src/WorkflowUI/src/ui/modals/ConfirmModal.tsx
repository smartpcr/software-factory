import { useCallback } from 'react';
import './ConfirmModal.css';

export interface ConfirmModalProps {
  title: string;
  body: string;
  confirmLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmModal({
  title,
  body,
  confirmLabel = 'Confirm',
  danger = false,
  onConfirm,
  onCancel,
}: ConfirmModalProps) {
  const handleBackdropClick = useCallback(
    (e: React.MouseEvent) => {
      if (e.target === e.currentTarget) onCancel();
    },
    [onCancel],
  );

  return (
    <div className="modal-backdrop" onClick={handleBackdropClick} data-testid="confirm-modal-backdrop">
      <div className="modal" role="dialog" aria-label={title} data-testid="confirm-modal">
        <h3>{title}</h3>
        <p>{body}</p>
        <div className="modal-actions">
          <button
            className="modal-btn-secondary"
            onClick={onCancel}
            data-testid="modal-cancel"
          >
            Cancel
          </button>
          <button
            className={danger ? 'modal-btn-danger' : 'modal-btn-primary'}
            onClick={onConfirm}
            data-testid="modal-confirm"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
