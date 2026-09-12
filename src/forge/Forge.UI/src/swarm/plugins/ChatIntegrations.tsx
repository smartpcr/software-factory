import { useCallback, useEffect, useRef, useState } from 'react';
import { chatApi, chatReason, chatRequest, type ChatConnection, type ChatDestination, type ChatHostStatus, type ChatManagement, type ChatSubscription } from './chatApi';
import { ChatConnectionEditor } from './ChatConnectionEditor';
import { ChatDestinationSetup } from './ChatDestinationSetup';
import { ChatIdentityLink } from './ChatIdentityLink';
import { ChatConnectionVerification, ChatDeliveryHistory, ChatPreview } from './ChatVerification';
import './chat.css';

export function ChatIntegrations() {
  const [management, setManagement] = useState<ChatManagement | null>(null);
  const [connections, setConnections] = useState<ChatConnection[]>([]);
  const [selected, setSelected] = useState('');
  const [channels, setChannels] = useState<ChatDestination[]>([]);
  const [editor, setEditor] = useState<{ id?: string; providerId: string } | null>(null);
  const [addingChannel, setAddingChannel] = useState(false);
  const [editorBusy, setEditorBusy] = useState(false);
  const [channelVersion, setChannelVersion] = useState(0);
  const [loadingChannels, setLoadingChannels] = useState(false);
  const channelOwner = useRef('');
  const [error, setError] = useState('');
  const refresh = useCallback(() => {
    void chatApi.management().then(async value => {
      setManagement(value);
      if (value.canManageConnections) setConnections(await chatApi.connections());
      else {
        const choices = (await Promise.all(value.products.map(p => chatApi.connections(p.id)))).flat();
        setConnections([...new Map(choices.map(c => [c.id, c])).values()]);
      }
      setError('');
    }).catch(e => setError(e.message));
  }, []);
  useEffect(refresh, [refresh]);
  useEffect(() => { window.addEventListener('focus', refresh); return () => window.removeEventListener('focus', refresh); }, [refresh]);
  const loadChannels = useCallback(() => setChannelVersion(value => value + 1), []);
  useEffect(() => {
    let active = true;
    if (channelOwner.current !== selected) { setChannels([]); channelOwner.current = selected; }
    setLoadingChannels(false);
    if (selected && management?.canManageConnections) {
      setLoadingChannels(true);
      void chatApi.destinations(undefined, selected).then(value => { if (active) setChannels(value); })
        .catch(e => { if (active) setError(e.message); }).finally(() => { if (active) setLoadingChannels(false); });
    }
    return () => { active = false; };
  }, [selected, management?.canManageConnections, channelVersion]);
  const connection = connections.find(c => c.id === selected);
  return <section className="chat-integrations swarm-ctrl" id="integrations-chat" aria-label="Chat integrations">
    <header className="chat-settings-header"><span className="chat-eyebrow">SETTINGS / INTEGRATIONS</span><h2>Chat</h2>
      <p>Connect Telegram and Slack once. Choose destinations independently for each story and for host alerts.</p></header>
    {error && <p className="chat-error" role="alert">{error}</p>}
    {management && <>
      {management.hostReason && <p className="chat-notice">{chatReason(management.hostReason)}</p>}
      <div className="chat-connection-workspace">
        <section className="chat-connection-library" aria-label="Saved connections">
          <header className="chat-section-heading"><div><span className="chat-eyebrow">YOUR CONNECTIONS</span><h3>Saved connections</h3></div>
            <span className="chat-count" aria-label={`${connections.length} saved connections`}>{connections.length.toString().padStart(2, '0')}</span></header>
          <p className="chat-help">Select a connection to manage its channels.</p>
          {!management.canManageConnections && <p className="chat-notice">A host administrator manages credentials and channels. Product owners manage destinations on each story.</p>}
          <ul className="chat-connections">{connections.map(c => <li key={c.id} data-selected={editor?.id === c.id || !editor && selected === c.id}>
            <button type="button" className="chat-connection-select" aria-label={`${c.displayName} ${c.providerId} · ${c.state}`}
              aria-pressed={editor?.id === c.id || !editor && selected === c.id} disabled={editorBusy || !management.canManageConnections}
              onClick={() => { setSelected(c.id); setEditor(null); setAddingChannel(false); }}>
              <ProviderIcon providerId={c.providerId} /><span className="chat-connection-copy"><strong title={c.displayName}>{c.displayName}</strong>
                <span className="chat-connection-meta"><span>{c.providerId === 'slack' ? 'Slack' : c.providerId === 'telegram' ? 'Telegram' : c.providerId}</span>
                  <span className="chat-connection-status" data-state={c.state}>{c.state}</span></span></span>
            </button>
            {management.canManageConnections && <button type="button" className="chat-connection-edit" aria-label="Repair / edit" disabled={editorBusy} title={`Repair or edit ${c.displayName}`}
              onClick={() => { setSelected(c.id); setAddingChannel(false); setEditor({ id: c.id, providerId: c.providerId }); }}>
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.3" aria-hidden="true"><path d="m10 3 3 3-7 7-4 1 1-4 7-7ZM9 4l3 3" /></svg><span>Repair / edit</span></button>}
            {c.reason && <p className="chat-notice">{chatReason(c.reason)}</p>}
          </li>)}</ul>
          {!connections.length && <div className="chat-library-empty"><span aria-hidden="true">＋</span><strong>No connections saved yet.</strong>
            <p>Choose Telegram or Slack to connect your first workspace.</p></div>}
          <p className="chat-library-footnote">Connections are shared. Each story chooses its own channels.</p>
        </section>
        {management.canManageConnections && <section className="chat-setup-area" aria-label="Connection workspace">
          <div className="chat-add-connection">
            <header className="chat-section-heading"><div><span className="chat-eyebrow">CONNECT A PROVIDER</span><h3>Add a connection</h3></div></header>
            <p className="chat-help">Connect your workspace or bot. Forge guides you through authorization.</p>
            <div className="chat-provider-picker">{['telegram', 'slack'].map(providerId => <button key={providerId} type="button" disabled={editorBusy}
              aria-label={`Add ${providerId === 'telegram' ? 'Telegram' : 'Slack'} connection`} aria-pressed={editor?.providerId === providerId && !editor.id}
              onClick={() => { setAddingChannel(false); setEditor({ providerId }); }}>
              <ProviderIcon providerId={providerId} /><span><strong>{providerId === 'telegram' ? 'Telegram' : 'Slack'}</strong><small>Add connection</small></span>
              <span className="chat-provider-indicator" aria-hidden="true">{editor?.providerId === providerId && !editor.id ? '✓' : '＋'}</span>
            </button>)}</div>
          </div>
          {editor ? <ChatConnectionEditor key={editor.id ?? editor.providerId} {...editor} management={management} onCancel={() => setEditor(null)} onBusyChange={setEditorBusy}
            onSaved={id => { setEditor(null); refresh(); setSelected(id); loadChannels(); }} />
            : connection ? <section className="chat-selected-connection" aria-label={`Channels for ${connection.displayName}`}>
              <header className="chat-section-heading"><div><span className="chat-eyebrow">CHANNELS & DELIVERY</span><h3>Channels · {connection.displayName}</h3></div></header>
              <p className="chat-help">Choose where Forge can send notifications and receive replies.</p>
              <button type="button" className="primary" onClick={() => setAddingChannel(true)}>Discover or add channel</button>
              {addingChannel && <ChatDestinationSetup key={connection.id} connection={connection} onCancel={() => setAddingChannel(false)} onSaved={() => { setAddingChannel(false); loadChannels(); }} />}
              {loadingChannels && <p className="chat-help" role="status">Loading channels…</p>}
              {!loadingChannels && !channels.length && !addingChannel && <p className="chat-help">No channels added to this connection yet.</p>}
              {channels.map(d => <ChatConnectionVerification key={`${connection.id}:${d.id}`} connectionId={connection.id} destination={d} onChanged={loadChannels} />)}
            </section> : <div className="chat-selection-hint"><span aria-hidden="true">↖</span><div><strong>Manage an existing connection</strong>
              <p>Select one from the saved list to view channels and check delivery.</p></div></div>}
        </section>}
      </div>
      <div className="chat-settings-grid"><ChatIdentityLink connections={connections} management={management} onRefresh={refresh} />
        <HostAlerts connections={connections} management={management} /></div>
    </>}
  </section>;
}

function ProviderIcon({ providerId }: { providerId: string }) {
  return <span className={`chat-provider-icon chat-provider-icon-${providerId}`} aria-hidden="true">
    {providerId === 'telegram' ? <svg viewBox="0 0 24 24" fill="none"><path d="m21 3-4 18-6-5-4 3 1-7L21 3ZM8 12l9-6-6 10" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" /></svg>
      : providerId === 'slack' ? <svg viewBox="0 0 24 24" fill="none" strokeWidth="3" strokeLinecap="round"><path d="M9 3v7M3 9h7" stroke="#36c5f0" /><path d="M14 3v7M14 9h7" stroke="#2eb67d" /><path d="M3 14h7M9 14v7" stroke="#e01e5a" /><path d="M14 14h7M14 14v7" stroke="#ecb22e" /></svg>
        : <span>↗</span>}
  </span>;
}

function HostAlerts({ connections, management }: { connections: ChatConnection[]; management: ChatManagement }) {
  const configurationRef = useRef<HTMLDetailsElement>(null);
  const [subscriptions, setSubscriptions] = useState<ChatSubscription[]>([]);
  const [status, setStatus] = useState<ChatHostStatus | null>(null);
  const [provider, setProvider] = useState('');
  const [connectionId, setConnectionId] = useState('');
  const [channels, setChannels] = useState<ChatDestination[]>([]);
  const [destinationId, setDestinationId] = useState('');
  const [contentMode, setContentMode] = useState('metadata');
  const [events, setEvents] = useState(['queue-strain', 'human-interaction']);
  const [editing, setEditing] = useState<ChatSubscription | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const allowed = management.canManageHostAlerts;
  const load = useCallback(async () => {
    if (!allowed) return;
    const [setup, status] = await Promise.all([chatRequest<{ subscriptions: ChatSubscription[] }>('/api/chat/host/subscriptions'), chatRequest<ChatHostStatus>('/api/chat/host/status')]);
    setSubscriptions(setup.subscriptions); setStatus(status);
  }, [allowed]);
  useEffect(() => { void load().catch(e => setError(e.message)); const timer = setInterval(() => { void load().catch(e => setError(e.message)); }, 10000); return () => clearInterval(timer); }, [load]);
  useEffect(() => { let active = true; setChannels([]); if (connectionId) void chatApi.destinations(undefined, connectionId).then(d => { if (active) setChannels(d); }).catch(e => { if (active) setError(e.message); }); return () => { active = false; }; }, [connectionId]);
  const choices = connections.filter(c => c.providerId === provider);
  async function save() {
    setBusy(true); setError('');
    try {
      await chatRequest(`/api/chat/host/subscriptions/${editing?.id ?? crypto.randomUUID()}`, 'PUT', {
        expectedRevision: editing?.revision ?? 0, parentRevision: 0, destinationId: editing?.destinationId ?? destinationId,
        conversationPolicy: editing?.conversationPolicy ?? 'flat', interactive: false, paused: false, contentMode, eventKinds: events,
      });
      setEditing(null); setDestinationId(''); await load();
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function retire(sub: ChatSubscription) {
    setBusy(true); setError('');
    try { await chatRequest(`/api/chat/host/subscriptions/${encodeURIComponent(sub.id)}?expectedRevision=${sub.revision}`, 'DELETE'); await load(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <section className="chat-host-alerts" aria-label="Host alerts"><header className="chat-section-heading"><div><span className="chat-eyebrow">HOST NOTIFICATIONS</span><h3>Host alerts</h3></div></header>
    <p className="chat-help">Queue pressure and host requests go here, including while no story is active.</p>
    {!allowed ? <p className="chat-notice">Host alert permission is required. Product ownership does not grant this permission.</p> : <>
      <p className="chat-stat"><span>Unrouted <strong>{Object.values(status?.chat.unrouted ?? {}).reduce((a, b) => a + b, 0)}</strong></span><span>Orphaned scopes <strong>{status?.orphaned ?? 0}</strong></span></p>
      <ul>{subscriptions.map(s => <li key={s.id}><strong>{s.destinationId}</strong> · {s.contentMode} · {JSON.parse(s.eventKindsJson).join(', ')}
        <div className="chat-actions"><button type="button" disabled={busy} onClick={() => { setEditing(s); setContentMode(s.contentMode); setEvents(JSON.parse(s.eventKindsJson)); if (configurationRef.current) configurationRef.current.open = true; }}>Edit alert filters</button>
          <button type="button" disabled={busy} onClick={() => void retire(s)}>Retire subscription</button></div></li>)}</ul>
      <details ref={configurationRef} className="chat-host-configuration"><summary>Configure host alerts</summary><fieldset disabled={busy}>
        {editing ? <p>Editing alert policy for {editing.destinationId}</p> : <>
          <label>Host provider<select value={provider} onChange={e => { setProvider(e.target.value); setConnectionId(connections.find(c => c.providerId === e.target.value)?.id ?? ''); setDestinationId(''); }}>
            <option value="">Choose provider</option><option value="telegram">Telegram</option><option value="slack">Slack</option></select></label>
          {choices.length > 1 && <label>Host connection<select value={connectionId} onChange={e => { setConnectionId(e.target.value); setDestinationId(''); }}>{choices.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}</select></label>}
          <label>Host channel<select value={destinationId} onChange={e => setDestinationId(e.target.value)}><option value="">Choose channel</option>{channels.map(d => <option key={d.id} value={d.id}>{d.displayName}</option>)}</select></label>
        </>}
        <label>Host content policy<select value={contentMode} onChange={e => setContentMode(e.target.value)}><option value="metadata">Metadata only</option><option value="redacted">Redacted summary</option></select></label>
        <strong>Event filters</strong>{['queue-strain', 'human-interaction', 'open-questions', 'workstream-stalled'].map(event => <label className="chat-checkline" key={event}>
          <input type="checkbox" checked={events.includes(event)} onChange={e => setEvents(old => e.target.checked ? [...old, event] : old.filter(k => k !== event))} />{event}</label>)}
        <div className="chat-actions"><button type="button" onClick={() => { setEditing(null); setDestinationId(''); setEvents(['queue-strain', 'human-interaction']); setContentMode('metadata'); }}>Cancel alert changes</button>
          <button type="button" disabled={!editing && !destinationId} onClick={() => void save()}>Save host alerts</button></div>
      </fieldset>
      {contentMode === 'redacted' && !management.hostRedactedContentAllowed && <p className="chat-notice">The host currently permits metadata only. A host content-policy change and refresh are required for redacted summaries.</p>}
      <ChatPreview contentMode={contentMode} />
      </details>
      {status && <ChatDeliveryHistory history={status.chat} canManage />}
      <p className="chat-help">Retiring a subscription leaves existing provider-held messages in place.</p>
    </>}
    {error && <p className="chat-error" role="alert">{error}</p>}
  </section>;
}