import { useEffect, useRef, useState } from 'react';

interface Props {
  open: boolean;
  logUrl: string;
  title?: string;
  onClose: () => void;
}

/**
 * Phase 59 — full-screen-ish modal that streams a gate log from the
 * server and renders it in a scrollable monospace pane. Follows the
 * project's `.modal` / `.modal-backdrop` pattern from
 * `toast-modal.css` with a wider width (90 vw) since gate logs are
 * wide text. Auto-scrolls to the bottom on initial load so the
 * operator sees the failure lines first.
 */
export function GateLogModal({ open, logUrl, title, onClose }: Props) {
  const [content, setContent] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const preRef = useRef<HTMLPreElement | null>(null);

  // Fetch log content when opened.
  useEffect(() => {
    if (!open || !logUrl) return;
    setLoading(true);
    setError(null);
    setContent(null);

    const ctrl = new AbortController();
    fetch(logUrl, { signal: ctrl.signal })
      .then(async (res) => {
        if (!res.ok) {
          setError(`${res.status} ${res.statusText}`);
          return;
        }
        const text = await res.text();
        setContent(text);
        // Auto-scroll to bottom after content renders.
        requestAnimationFrame(() => {
          if (preRef.current) {
            preRef.current.scrollTop = preRef.current.scrollHeight;
          }
        });
      })
      .catch((e) => {
        if (e.name !== 'AbortError') {
          setError(e instanceof Error ? e.message : 'Failed to fetch log');
        }
      })
      .finally(() => setLoading(false));

    return () => ctrl.abort();
  }, [open, logUrl]);

  // Esc to close.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={onClose}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="gate-log-title"
        style={{
          width: 'min(960px, 92vw)',
          maxHeight: '85vh',
          display: 'flex',
          flexDirection: 'column',
          padding: 0,
        }}
      >
        {/* Header */}
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '14px 18px 10px',
            borderBottom: '1px solid var(--border, rgba(255,255,255,0.08))',
            flexShrink: 0,
          }}
        >
          <h3
            id="gate-log-title"
            className="modal-title"
            style={{ margin: 0, fontSize: 14, fontWeight: 600 }}
          >
            {title || 'Gate Log'}
          </h3>
          <button
            onClick={onClose}
            style={{
              background: 'transparent',
              border: 'none',
              color: 'var(--text-secondary, #9ca3af)',
              cursor: 'pointer',
              fontSize: 16,
              padding: '2px 6px',
              borderRadius: 3,
              lineHeight: 1,
            }}
            title="Close (Esc)"
            aria-label="Close"
          >
            ✕
          </button>
        </div>

        {/* Body */}
        <div style={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          {loading && (
            <div style={{
              padding: 24,
              color: 'var(--text-tertiary, #6b7280)',
              fontSize: 12,
              textAlign: 'center',
            }}>
              Loading log…
            </div>
          )}
          {error && (
            <div style={{
              padding: 24,
              color: 'var(--danger, #f87171)',
              fontSize: 12,
              textAlign: 'center',
            }}>
              Failed to load log: {error}
            </div>
          )}
          {content != null && (
            <pre
              ref={preRef}
              style={{
                flex: 1,
                margin: 0,
                padding: '12px 18px',
                overflow: 'auto',
                fontSize: 11,
                lineHeight: 1.5,
                fontFamily: 'var(--font-mono, monospace)',
                color: 'var(--text-primary, currentColor)',
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                background: 'var(--bg-sunken, rgba(0,0,0,0.15))',
              }}
            >
              {content || '(empty log)'}
            </pre>
          )}
        </div>
      </div>
    </>
  );
}