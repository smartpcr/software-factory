import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { chatApi, chatReason, type ChatConnection, type ChatDestination, type ChatSetup } from './chatApi';
import './chat.css';

interface Props { productId: string; spawnId: string; title: string }

export function ChatRowControl({ productId, spawnId, title }: Props) {
  const [setup, setSetup] = useState<ChatSetup | null>(null);
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    const refresh = () => chatApi.get(productId, spawnId).then(value => {
      if (!cancelled) { setSetup(current => current && current.revision > value.revision ? current : value); setError(null); }
    }).catch(() => { if (!cancelled) setError('Chat status is unavailable.'); });
    void refresh();
    const timer = window.setInterval(() => { if (!document.hidden) void refresh(); }, 5000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [productId, spawnId]);
  const state = error ? 'needs-setup' : setup?.state;
  return <span className="chat-row-control" onClick={e => e.stopPropagation()}>
    <button type="button" className={`chat-row-button chat-state-${state ?? 'loading'}`}
      aria-label={`Chat for ${title}`} title={error ?? chatReason(setup?.reason ?? null)}
      onClick={() => setOpen(true)}>
      <span className="chat-state-dot" aria-hidden="true" />Chat
      <span className="chat-row-state">{state === 'needs-setup' ? 'Needs setup' : state === 'ready' ? 'Ready' : state === 'off' ? 'Off' : '…'}</span>
    </button>
    {open && createPortal(<ChatPanel productId={productId} spawnId={spawnId} title={title}
      onClose={() => setOpen(false)} onSaved={setSetup} />, document.body)}
  </span>;
}

function ChatPanel({ productId, spawnId, title, onClose, onSaved }: Props & { onClose: () => void; onSaved: (setup: ChatSetup) => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [saved, setSaved] = useState<ChatSetup | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [connections, setConnections] = useState<ChatConnection[]>([]);
  const [connectionId, setConnectionId] = useState('');
  const [destinations, setDestinations] = useState<ChatDestination[]>([]);
  const [destinationId, setDestinationId] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loadingChannels, setLoadingChannels] = useState(false);
  useEffect(() => {
    dialog.current?.showModal();
    let cancelled = false;
    void Promise.all([chatApi.get(productId, spawnId), chatApi.connections(productId)]).then(([value, choices]) => {
      if (cancelled) return;
      setSaved(value); setEnabled(value.enabled); setDestinationId(value.destinationId ?? '');
      setConnections(choices.filter(c => c.providerId === 'telegram' && c.enabled));
      setConnectionId(value.connectionId ?? '');
    }).catch(err => { if (!cancelled) setError((err as Error).message); });
    return () => { cancelled = true; };
  }, [productId, spawnId]);
  useEffect(() => {
    let cancelled = false;
    setDestinations([]);
    setLoadingChannels(false);
    if (!connectionId) return;
    setLoadingChannels(true);
    void chatApi.destinations(productId, connectionId).then(value => {
      if (!cancelled) setDestinations(value);
    }).catch(err => { if (!cancelled) setError((err as Error).message); })
      .finally(() => { if (!cancelled) setLoadingChannels(false); });
    return () => { cancelled = true; };
  }, [productId, connectionId]);
  async function save() {
    if (!saved) return;
    setBusy(true); setError(null);
    try {
      const value = await chatApi.save(productId, spawnId, { expectedRevision: saved.revision, enabled,
        destinationId: enabled ? destinationId : null, activateNewRun: saved.reason === 'new-run-needs-activation' });
      onSaved(value); onClose();
    } catch (err) { setError((err as Error).message); }
    finally { setBusy(false); }
  }
  const canSave = saved?.canManage && !busy && (!enabled || !!connectionId && destinations.some(d => d.id === destinationId));
  return <dialog ref={dialog} className="chat-panel swarm-ctrl" aria-labelledby="chat-panel-title"
    onClick={e => e.stopPropagation()} onCancel={e => { e.preventDefault(); if (!busy) onClose(); }}>
    <form onSubmit={e => { e.preventDefault(); void save(); }}>
      <header className="chat-panel-header"><span className="chat-eyebrow">STORY PLUGIN / CHAT</span>
        <button type="button" className="chat-close" aria-label="Close Chat" disabled={busy} onClick={onClose}>×</button>
        <h2 id="chat-panel-title">Keep this story in view</h2><p className="chat-story-title">{title}</p>
      </header>
      <div className="chat-panel-body">
        <div className="chat-mode"><span>Notifications only</span><span>TELEGRAM</span></div>
        <label className="chat-toggle-line"><span><strong>Notify me about open questions</strong>
          <small>Applies to this story and its current and future workstreams.</small></span>
          <input type="checkbox" role="switch" aria-label="Enable notifications" checked={enabled}
            disabled={!saved?.canManage || busy} onChange={e => setEnabled(e.target.checked)} />
        </label>
        {saved?.reason && <p className="chat-notice" role="status">{chatReason(saved.reason)}</p>}
        {saved && !saved.canManage && <p className="chat-notice">A product owner can change these settings.</p>}
        <fieldset disabled={!enabled || !saved?.canManage || busy}>
          <label htmlFor="chat-provider">Provider / connection</label>
          <select id="chat-provider" value={connectionId} onChange={e => { setConnectionId(e.target.value); setDestinationId(''); }}>
            <option value="">Choose a Telegram connection</option>
            {connections.map(c => <option key={c.id} value={c.id}>Telegram · {c.displayName}</option>)}
          </select>
          <label htmlFor="chat-channel">Channel</label>
          <select id="chat-channel" value={destinationId} disabled={!connectionId || loadingChannels}
            onChange={e => setDestinationId(e.target.value)}>
            <option value="">{loadingChannels ? 'Loading channels…' : 'Choose a channel'}</option>
            {destinations.map(d => <option key={d.id} value={d.id}>{d.displayName}</option>)}
          </select>
          {saved && connections.length === 0 && <p className="chat-help">Ask a host administrator to add a test Telegram connection and grant it to this product.</p>}
          {!!connectionId && !loadingChannels && destinations.length === 0 && <p className="chat-help">This connection needs a channel. Ask a host administrator to add the isolated test destination.</p>}
        </fieldset>
        <div className="chat-preview"><span className="chat-eyebrow">MESSAGE PREVIEW · METADATA ONLY</span>
          <pre>{saved?.preview ?? 'Loading preview…'}</pre>
          <p>Question text, answer choices, logs and source stay in Forge.</p>
        </div>
        <p className="chat-help">Saving with notifications enabled sends an introduction. Turning notifications off cancels queued alerts. Delivered messages remain in Telegram.</p>
        {saved?.lastSentAt && <p className="chat-last-sent">Last sent {new Date(saved.lastSentAt).toLocaleString()}</p>}
        {error && <p className="chat-error" role="alert">{error}</p>}
      </div>
      <footer className="chat-panel-footer"><button type="button" className="btn" disabled={busy} onClick={onClose}>Cancel</button>
        <button type="submit" className="btn primary" disabled={!canSave}>{busy ? 'Saving…' : 'Save notifications'}</button></footer>
    </form>
  </dialog>;
}
