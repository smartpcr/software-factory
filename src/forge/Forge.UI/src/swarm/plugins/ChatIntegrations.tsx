import { useCallback, useEffect, useState } from 'react';
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
  const loadChannels = useCallback(() => {
    if (selected && management?.canManageConnections) void chatApi.destinations(undefined, selected).then(setChannels).catch(e => setError(e.message));
    else setChannels([]);
  }, [selected, management?.canManageConnections]);
  useEffect(loadChannels, [loadChannels]);
  const connection = connections.find(c => c.id === selected);
  return <section className="chat-integrations swarm-ctrl" id="integrations-chat" aria-label="Chat integrations">
    <header className="chat-settings-header"><span className="chat-eyebrow">SETTINGS / INTEGRATIONS</span><h2>Chat</h2>
      <p>Connect Telegram and Slack once. Choose destinations independently for each story and for host alerts.</p></header>
    {error && <p className="chat-error" role="alert">{error}</p>}
    {management && <>
      {management.hostReason && <p className="chat-notice">{chatReason(management.hostReason)}</p>}
      <p className="chat-help">Saved connection and subscription revisions apply live within the runtime refresh interval. Engine selection, process environment, and host content limits require a host refresh.</p>
      <div className="chat-settings-grid"><section><h3>Connections</h3>
        {!management.canManageConnections && <p className="chat-notice">A host administrator manages credentials and channels. Product owners manage destinations on each story.</p>}
        {management.canManageConnections && <>
          <div className="chat-actions"><button type="button" onClick={() => setEditor({ providerId: 'telegram' })}>Add Telegram connection</button>
            <button type="button" onClick={() => setEditor({ providerId: 'slack' })}>Add Slack connection</button></div>
          <ul className="chat-connections">{connections.map(c => <li key={c.id}><button type="button" aria-pressed={selected === c.id} onClick={() => { setSelected(c.id); setAddingChannel(false); }}>
            <strong>{c.displayName}</strong><span>{c.providerId} · {c.state}</span></button><button type="button" onClick={() => setEditor({ id: c.id, providerId: c.providerId })}>Repair / edit</button>
            {c.reason && <p className="chat-notice">{chatReason(c.reason)}</p>}</li>)}</ul>
          {!connections.length && <p className="chat-help">No connections saved yet.</p>}
          {editor && <ChatConnectionEditor key={editor.id ?? editor.providerId} {...editor} management={management} onCancel={() => setEditor(null)}
            onSaved={id => { setEditor(null); refresh(); setSelected(id); loadChannels(); }} />}
          {connection && <><h3>Channels · {connection.displayName}</h3><button type="button" onClick={() => setAddingChannel(true)}>Discover or add channel</button>
            {addingChannel && <ChatDestinationSetup key={connection.id} connection={connection} onCancel={() => setAddingChannel(false)} onSaved={() => { setAddingChannel(false); loadChannels(); }} />}
            {channels.map(d => <ChatConnectionVerification key={`${connection.id}:${d.id}`} connectionId={connection.id} destination={d} onChanged={loadChannels} />)}
          </>}
        </>}
        <ChatIdentityLink connections={connections} management={management} onRefresh={refresh} />
      </section><HostAlerts connections={connections} management={management} /></div>
    </>}
  </section>;
}

function HostAlerts({ connections, management }: { connections: ChatConnection[]; management: ChatManagement }) {
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
  return <section className="chat-host-alerts" aria-label="Host alerts"><span className="chat-eyebrow">PERMANENT HOST SCOPE</span><h3>Host alerts</h3>
    <p className="chat-help">Queue pressure and host requests go here, including while no story is active.</p>
    {!allowed ? <p className="chat-notice">Host alert permission is required. Product ownership does not grant this permission.</p> : <>
      <p className="chat-stat">Unrouted {Object.values(status?.chat.unrouted ?? {}).reduce((a, b) => a + b, 0)} <span>Orphaned scopes {status?.orphaned ?? 0}</span></p>
      <ul>{subscriptions.map(s => <li key={s.id}><strong>{s.destinationId}</strong> · {s.contentMode} · {JSON.parse(s.eventKindsJson).join(', ')}
        <div className="chat-actions"><button type="button" disabled={busy} onClick={() => { setEditing(s); setContentMode(s.contentMode); setEvents(JSON.parse(s.eventKindsJson)); }}>Edit alert filters</button>
          <button type="button" disabled={busy} onClick={() => void retire(s)}>Retire subscription</button></div></li>)}</ul>
      <fieldset disabled={busy}>
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
      {status && <ChatDeliveryHistory history={status.chat} canManage />}
      <p className="chat-help">Retiring a subscription leaves existing provider-held messages in place.</p>
    </>}
    {error && <p className="chat-error" role="alert">{error}</p>}
  </section>;
}