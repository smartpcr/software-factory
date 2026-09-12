import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { chatApi, chatRequest, connectionPath, type ChatConnection, type ChatDestination, type ChatDiscovery } from './chatApi';

export function ChatDestinationSetup({ connection, onSaved, onCancel, actionsTarget, onBusyChange }: { connection: ChatConnection; onSaved: (d: ChatDestination) => void; onCancel: () => void; actionsTarget?: HTMLElement | null; onBusyChange?: (busy: boolean) => void }) {
  const [tenant, setTenant] = useState('');
  const [channel, setChannel] = useState('');
  const [name, setName] = useState('');
  const [kind, setKind] = useState(connection.providerId === 'slack' ? 'channel' : 'forum');
  const [items, setItems] = useState<ChatDiscovery['items']>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => { onBusyChange?.(busy); }, [busy, onBusyChange]);
  useEffect(() => () => onBusyChange?.(false), [onBusyChange]);
  useEffect(() => { let active = true; void chatApi.details(connection.id).then(c => { if (active) setTenant(c.installationId); }).catch(e => { if (active) setError(e.message); }); return () => { active = false; }; }, [connection.id]);
  async function discover(next?: string) {
    setBusy(true); setError('');
    try { const page = await chatApi.discover(connection.id, next); setItems(old => next ? [...old, ...page.items] : page.items); setCursor(page.nextCursor); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function save() {
    setBusy(true); setError('');
    try {
      const existing = (await chatApi.destinations(undefined, connection.id)).find(d => d.channelId === channel && d.externalTenantId === tenant);
      const saved = existing ?? await chatRequest<ChatDestination>(connectionPath(connection.id) + `/destinations/${crypto.randomUUID()}`, 'PUT',
        { expectedRevision: 0, externalTenantId: tenant, channelId: channel, displayName: name, kind });
      await chatApi.validate(connection.id, saved.id);
      const verified = (await chatApi.destinations(undefined, connection.id)).find(d => d.id === saved.id)!;
      onSaved(verified);
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  const actions = <div className="chat-actions chat-editor-actions"><button type="button" disabled={busy} onClick={onCancel}>Cancel channel setup</button>
    <button type="button" className="primary" disabled={busy || !name.trim() || !channel.trim() || !tenant} onClick={() => void save()}>Save channel</button></div>;
  return <section className="chat-card chat-channel-editor" aria-label="Channel setup"><h3>Add channel · {connection.displayName}</h3>
    <p className="chat-help">Save records this channel and validates access. Test sends are a separate action.</p>
    <fieldset disabled={busy}>
      <details className="chat-section" open><summary>Channel discovery</summary><div className="chat-section-content">
        <button type="button" data-chat-initial-focus onClick={() => void discover()}>Discover channels</button>
        <label>Find channel by name<input type="search" value={filter} onChange={e => setFilter(e.target.value)} /></label>
        <div className="chat-discovered">{items.filter(i => i.displayName.toLowerCase().includes(filter.toLowerCase())).map(i => <button type="button" key={i.address.channelId}
          onClick={() => { setTenant(i.address.externalTenantId); setChannel(i.address.channelId); setName(i.displayName); setKind(i.kind); }}>{i.displayName}</button>)}</div>
        {cursor && <button type="button" onClick={() => void discover(cursor)}>Load more channels</button>}
      </div></details>
      <details className="chat-section" open><summary>Channel details</summary><div className="chat-section-content">
        <label>Channel name<input value={name} onChange={e => setName(e.target.value)} /></label>
        <label>Channel ID<input value={channel} onChange={e => setChannel(e.target.value)} /></label>
        <label>Channel type<select value={kind} onChange={e => setKind(e.target.value)}>{['forum', 'supergroup', 'private', 'channel', 'group'].map(k => <option key={k}>{k}</option>)}</select></label>
        {connection.providerId === 'telegram' && <p className="chat-help">Use a forum group with Topics enabled and grant the bot Manage Topics. If discovery is unavailable, enter the group ID from Telegram.</p>}
      </div></details>
    </fieldset>
    {error && <p className="chat-error" role="alert">{error}</p>}
    {actionsTarget ? createPortal(actions, actionsTarget) : actions}
  </section>;
}