import { useToastStore } from '@/state/useToastStore';

// Renders toasts using the prototype's existing .toast class. Only the most
// recent toast is visible — the prototype shows one at a time too.
export function Toaster() {
  const toasts = useToastStore((s) => s.toasts);
  const last = toasts[toasts.length - 1];

  return (
    <div
      className={`toast${last ? ` show ${last.kind}` : ''}`}
      id="toast"
      role="status"
      aria-live="polite"
    >
      <span className="toast-dot" />
      <span id="toast-msg">{last?.msg ?? ''}</span>
    </div>
  );
}
