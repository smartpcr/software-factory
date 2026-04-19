import { useToastStore } from './toastStore';
import './Toasts.css';

export function ToastContainer() {
  const toasts = useToastStore((s) => s.toasts);

  if (toasts.length === 0) return null;

  return (
    <div className="toasts" data-testid="toast-container" role="status" aria-live="polite" aria-label="Notifications">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.kind}`} data-testid={`toast-${t.id}`}>
          <span>{t.message}</span>
        </div>
      ))}
    </div>
  );
}
