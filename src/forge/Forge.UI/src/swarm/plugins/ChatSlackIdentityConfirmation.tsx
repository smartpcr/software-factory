import { useEffect, useId, useRef } from 'react';
import { createPortal } from 'react-dom';

export function ChatSlackIdentityConfirmation({ channelName, url, requested, expired, busy, error, onRetry, onClose }: {
  channelName: string; url: string; requested: boolean; expired: boolean; busy: boolean; error: string;
  onRetry: () => void; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const title = useId();
  useEffect(() => { const element = dialog.current!; element.showModal(); return () => element.close(); }, []);
  return createPortal(<dialog ref={dialog} className="chat-panel swarm-ctrl chat-slack-identity-confirmation" aria-labelledby={title}
    onCancel={e => { e.preventDefault(); e.stopPropagation(); if (!busy) onClose(); }}>
    <header className="chat-panel-header"><span className="chat-eyebrow">ACCOUNT CONFIRMATION</span>
      <h2 id={title}>Confirm your Slack account</h2>
      <button type="button" className="chat-close" aria-label="Close account confirmation" disabled={busy} onClick={onClose}>×</button>
    </header>
    <div className="chat-panel-body">
      <ol className="chat-bootstrap-steps">
        <li><strong>Open {channelName} in Slack</strong><p>Keep Slack open with the account you want to link.</p>
          <a href={url} target="_blank" rel="noopener noreferrer">Open Slack confirmation channel ↗</a></li>
        <li><strong>Choose Confirm link</strong><p>Forge requests a message visible only to you. Select its Confirm link button, then Link account in Slack’s dialog.</p></li>
        <li><strong>Return to Forge</strong><p>This dialog closes when Forge verifies your approval. No command or confirmation code to copy.</p></li>
      </ol>
      {busy && <p role="status">Requesting confirmation…</p>}
      {!busy && requested && !expired && <p role="status">Slack accepted the request. Waiting for your approval.</p>}
      {expired && <p className="chat-notice" role="status">Confirmation expired. Request a new one below.</p>}
      <p className="chat-help">If no message appears, keep the channel open and select Send a new confirmation. Slack’s private messages disappear when the session reloads.</p>
      {error && <p className="chat-error" role="alert">{error}</p>}
    </div>
    <footer className="chat-panel-footer"><button type="button" disabled={busy} onClick={onClose}>Cancel linking</button>
      <button type="button" className="primary" disabled={busy} onClick={onRetry}>Send a new confirmation</button></footer>
  </dialog>, document.body);
}