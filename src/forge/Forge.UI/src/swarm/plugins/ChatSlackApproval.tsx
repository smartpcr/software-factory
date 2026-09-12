import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

const workspaceSignupUrl = 'https://slack.com/get-started#/createnew';

export function ChatSlackApproval({ approvalRequest, expiresAt, busy, error, onApprove, onRestart, onClose }: {
  approvalRequest: string; expiresAt: string; busy: boolean; error: string;
  onApprove: (code: string) => void; onRestart: () => void; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const cancelCopy = useRef<() => void>(() => {});
  const title = useId();
  const [code, setCode] = useState('');
  const [creatingWorkspace, setCreatingWorkspace] = useState(false);
  const [copyState, setCopyState] = useState<'idle' | 'copying' | 'copied' | 'blocked'>('idle');
  const [now, setNow] = useState(Date.now());
  const expired = now >= Date.parse(expiresAt);
  useEffect(() => {
    const element = dialog.current!; element.showModal();
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => { clearInterval(timer); cancelCopy.current(); element.close(); };
  }, []);
  function openSlack() {
    cancelCopy.current();
    setCopyState('copying');
    let active = true;
    const timer = window.setTimeout(() => finishCopy('blocked'), 2000);
    function finishCopy(state: 'copied' | 'blocked') {
      if (!active) return;
      active = false; window.clearTimeout(timer); setCopyState(state);
    }
    cancelCopy.current = () => { active = false; window.clearTimeout(timer); };
    // Request clipboard access before the new window takes focus, but never wait
    // for browser permission before opening Slack within this click gesture.
    try { navigator.clipboard.writeText(approvalRequest).then(() => finishCopy('copied'), () => finishCopy('blocked')); }
    catch { finishCopy('blocked'); }
    try { window.open('https://app.slack.com/client/', '_blank', 'noopener,noreferrer,popup,width=950,height=800'); }
    catch { /* The direct link below remains available if the browser blocks opening. */ }
  }
  function createWorkspace() {
    cancelCopy.current(); setCopyState('idle'); setCode(''); setCreatingWorkspace(true);
    try { window.open(workspaceSignupUrl, '_blank', 'noopener,noreferrer,popup,width=950,height=800'); }
    catch { /* The signup link remains available in the workspace prompt. */ }
  }
  return createPortal(<dialog ref={dialog} className="chat-panel swarm-ctrl chat-slack-approval" aria-labelledby={title}
    onCancel={event => { event.preventDefault(); event.stopPropagation(); if (!busy) onClose(); }}>
    <form onSubmit={event => { event.preventDefault(); if (creatingWorkspace || busy || expired || !code.trim()) return; const value = code.trim(); setCode(''); onApprove(value); }}>
      <header className="chat-panel-header"><span className="chat-eyebrow">SLACK AUTHORIZATION</span>
        <h2 id={title}>{creatingWorkspace ? 'Create a Slack workspace' : 'Approve Slack setup'}</h2>
        <p className="chat-story-title">{creatingWorkspace ? 'Create a workspace you own in Slack, then return here to connect Forge.'
          : 'Forge will create the app, configure its permissions and Socket Mode, and retrieve its credentials.'}</p>
        <button type="button" className="chat-close" aria-label="Close Slack approval" disabled={busy} onClick={onClose}>×</button></header>
      <div className="chat-panel-body">
        {creatingWorkspace ? <>
          <ol className="chat-bootstrap-steps">
            <li><strong>Sign in or create your Slack account</strong><p>Use your email, Google or Apple in Slack. Enter any email verification code in Slack.</p></li>
            <li><strong>Create and name your workspace</strong><p>Choose Create a workspace and follow Slack’s setup. You can name it Forge. You become its owner.</p></li>
            <li><strong>Return when your workspace is open</strong><p>Select Workspace ready, continue below. Forge starts a fresh approval request when you return.</p></li>
          </ol>
          <p><a href={workspaceSignupUrl} target="_blank" rel="noopener noreferrer">Open Slack signup if no window appeared</a></p>
          <p className="chat-help">If Slack asks for an existing workspace URL, use Create a new workspace on that page.</p>
        </> : <>
        <section className="chat-slack-workspace-choice" aria-label="Workspace options">
          <strong>Need a workspace you can manage?</strong>
          <p>Create a workspace for Forge, or find one linked to your email.</p>
          <div className="chat-actions"><button type="button" disabled={busy} onClick={createWorkspace}>Create a new Slack workspace</button>
            <a href="https://slack.com/signin" target="_blank" rel="noopener noreferrer">Find my workspaces</a></div>
        </section>
        <ol className="chat-bootstrap-steps">
          <li><strong>Open your Slack workspace</strong>
            <button type="button" className="primary chat-bootstrap-link" disabled={busy || expired} onClick={openSlack}>Copy approval request and open Slack</button>
            <p><a href="https://app.slack.com/client/" target="_blank" rel="noopener noreferrer">Open Slack if no window appeared</a></p>
            {copyState === 'copying' && <p role="status">Copying approval request…</p>}
            {copyState === 'copied' && <p role="status">Approval request copied. Continue in Slack.</p>}
            {copyState === 'blocked' && <label>Slack approval request<textarea readOnly value={approvalRequest} onFocus={event => event.currentTarget.select()} />
              <span className="chat-help" role="status">The browser did not confirm copying. Copy this request, then continue in Slack.</span></label>}
          </li>
          <li><strong>Approve in Slack’s permission dialog</strong>
            <p>Paste the copied request into a conversation in the workspace you want to connect, then press Enter. Slack opens a private permission dialog. Review it and select <strong>Confirm</strong>.</p>
          </li>
          <li><strong>Return Slack’s confirmation</strong>
            <p>Slack displays a confirmation code after approval. Paste it here; Forge handles the app setup next.</p>
            <label>Slack confirmation code<input autoComplete="off" type="password" spellCheck={false} maxLength={256} value={code}
              disabled={busy || expired} onChange={event => setCode(event.target.value)} placeholder="Paste the code shown by Slack" /></label>
          </li>
        </ol>
        {expired ? <div className="chat-error" role="alert"><p>This approval expired. Get a fresh request to continue.</p>
          <button type="button" disabled={busy} onClick={onRestart}>Refresh approval request</button></div>
          : <p className="chat-help">Expires at {new Date(expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}. Only approve a request you started in Forge on this host.</p>}
        <p className="chat-help">Slack’s developer authorization permits app creation. Forge uses it only for this setup and keeps the resulting bot credentials encrypted when you save.</p>
        </>}
        {error && <p className="chat-error" role="alert">{error}</p>}
      </div>
      <footer className="chat-panel-footer chat-actions"><button type="button" disabled={busy} onClick={onClose}>Back to connection</button>
        {creatingWorkspace ? <button type="button" className="primary" disabled={busy} onClick={onRestart}>{busy ? 'Preparing approval…' : 'Workspace ready, continue'}</button>
          : <button type="submit" className="primary" disabled={busy || expired || !code.trim()}>{busy ? expired ? 'Preparing approval…' : 'Configuring Slack app…' : 'Verify and configure automatically'}</button>}</footer>
    </form>
  </dialog>, document.body);
}