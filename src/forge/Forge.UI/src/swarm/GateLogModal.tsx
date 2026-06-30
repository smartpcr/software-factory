import { useEffect, useRef, useState } from 'react';
import { buildScopeHeaders, getActiveProductId, getActiveRepoId } from '../api/forgeApi';

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
  const [copied, setCopied] = useState(false);
  const preRef = useRef<HTMLPreElement | null>(null);

  // Reset the transient "copied" affordance whenever the modal is
  // re-opened or pointed at a different log.
  useEffect(() => { setCopied(false); }, [open, logUrl]);

  const hasContent = content != null && content.length > 0;

  async function handleCopy() {
    if (content == null) return;
    try {
      await navigator.clipboard.writeText(content);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard API can reject (insecure context / denied permission);
      // fall back to a hidden textarea + execCommand so Copy still works.
      try {
        const ta = document.createElement('textarea');
        ta.value = content;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
        setCopied(true);
        window.setTimeout(() => setCopied(false), 1500);
      } catch { /* give up silently */ }
    }
  }

  function handleSave() {
    if (content == null) return;
    const blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = deriveLogFilename(title);
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    // Revoke on the next tick so the click has consumed the URL.
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
  }

  // Fetch log content when opened.
  useEffect(() => {
    if (!open || !logUrl) return;
    setLoading(true);
    setError(null);
    setContent(null);

    const ctrl = new AbortController();
    // 2026-05-21 — the gate-log endpoint (GET /api/workstreams/{id}/logs/{*relPath})
    // resolves the workstream through a product-scoped ProductDbContext, so it
    // needs the same X-Forge-Product-Id / X-Forge-Repo-Id headers the typed
    // forgeApi client sends. A bare fetch() omitted them, so the server bound
    // the default product DB, the workstream row was absent, and every log
    // request 404'd ("Failed to load log: 404 Not Found") even though the
    // file was on disk.
    fetch(logUrl, {
      signal: ctrl.signal,
      headers: buildScopeHeaders(getActiveProductId(), getActiveRepoId()),
    })
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
            style={{ margin: 0, fontSize: 14, fontWeight: 600, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
          >
            {title || 'Gate Log'}
          </h3>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
            <button
              onClick={handleCopy}
              disabled={!hasContent}
              style={logActionBtnStyle(!hasContent)}
              title="Copy all logs to clipboard"
              aria-label="Copy all logs"
            >
              {copied ? '✓ Copied' : 'Copy'}
            </button>
            <button
              onClick={handleSave}
              disabled={!hasContent}
              style={logActionBtnStyle(!hasContent)}
              title="Save all logs to a file"
              aria-label="Save all logs"
            >
              Save
            </button>
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

// Turn the human modal title ("test · iter 1 · log") into a safe download
// filename ("test-iter-1-log.log"). Strips anything that isn't a word char,
// dash, or dot; collapses runs to a single dash; always ends in `.log`.
function deriveLogFilename(title?: string): string {
  const base = (title || 'gate-log')
    .normalize('NFKD')
    .replace(/[^\w.-]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^[-.]+|[-.]+$/g, '')
    .toLowerCase();
  const safe = base || 'gate-log';
  return safe.endsWith('.log') ? safe : `${safe}.log`;
}

function logActionBtnStyle(disabled: boolean): React.CSSProperties {
  return {
    background: 'transparent',
    border: '1px solid var(--border-strong, rgba(255,255,255,0.18))',
    color: disabled ? 'var(--text-tertiary, #6b7280)' : 'var(--text-secondary, #9ca3af)',
    cursor: disabled ? 'default' : 'pointer',
    fontSize: 11,
    fontWeight: 600,
    letterSpacing: 0.3,
    padding: '3px 9px',
    borderRadius: 4,
    lineHeight: 1.4,
    opacity: disabled ? 0.5 : 1,
    whiteSpace: 'nowrap',
  };
}