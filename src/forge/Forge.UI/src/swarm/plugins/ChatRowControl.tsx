import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { chatApi, chatReason, type ChatConnection, type ChatDestination, type ChatManagement, type ChatRoute, type ChatSetup, type ChatSummary } from './chatApi';
import { reloadChatSummaries, useChatSummary } from './chatStore';
import { ChatConnectionEditor } from './ChatConnectionEditor';
import { ChatDestinationSetup } from './ChatDestinationSetup';
import { ChatIdentityLink } from './ChatIdentityLink';
import { ChatDeliveryHistory, ChatPreview } from './ChatVerification';
import './chat.css';

interface Props { productId: string; spawnId: string; title: string; summary?: ChatSummary; compact?: boolean }
export interface ChatRouteDraft extends ChatRoute { key: string; providerId: string; connectionId: string }
export const emptyChatRoute = (interactive = true): ChatRouteDraft => ({ key: crypto.randomUUID(), providerId: '', connectionId: '', destinationId: '', conversationPolicy: 'flat', interactive, contentMode: 'metadata' });
export function changeChatProvider(route: ChatRouteDraft, providerId: string, connections: ChatConnection[]): ChatRouteDraft {
  return { ...route, providerId, connectionId: connections.find(c => c.providerId === providerId)?.id ?? '', destinationId: '', conversationPolicy: providerId === 'slack' ? 'thread' : 'topic' };
}
export function ChatRouteFields({ route, connections, destinations, canManageConnections, notificationOnlySlice, onChange, onSetup, onChannelSetup, onRemove }: {
  route: ChatRouteDraft; connections: ChatConnection[]; destinations: ChatDestination[]; canManageConnections: boolean; notificationOnlySlice?: boolean;
  onChange: (r: ChatRouteDraft) => void; onSetup: (trigger: HTMLButtonElement) => void; onChannelSetup: (trigger: HTMLButtonElement) => void; onRemove: () => void;
}) {
  const choices = connections.filter(c => c.providerId === route.providerId);
  const channels = destinations.filter(d => d.connectionId === route.connectionId);
  const [filter, setFilter] = useState('');
  return <section className="chat-route" aria-label="Destination">
    <div className="chat-route-top"><span>PROVIDER / CHANNEL</span><button type="button" aria-label="Remove destination" onClick={onRemove}>Remove</button></div>
    <label>Provider<select value={route.providerId} onChange={e => { setFilter(''); onChange(changeChatProvider(route, e.target.value, connections)); }}>
      <option value="">Choose provider</option><option value="telegram">Telegram</option><option value="slack" disabled={notificationOnlySlice}>Slack</option></select></label>
    {choices.length > 1 && <label>Connection<select value={route.connectionId} onChange={e => onChange({ ...route, connectionId: e.target.value, destinationId: '' })}>
      <option value="">Choose connection</option>{choices.map(c => <option key={c.id} value={c.id}>{c.displayName}{c.enabled ? '' : ' · disabled'}</option>)}</select></label>}
    {!!route.providerId && !choices.length && <p className="chat-notice">No {route.providerId} connection is available for this product.</p>}
    {!!route.providerId && canManageConnections && <button type="button" className="chat-text-button" onClick={e => onSetup(e.currentTarget)}>Set up connection</button>}
    {!!route.connectionId && <>
      {connections.find(c => c.id === route.connectionId)?.enabled === false && <p className="chat-notice">This connection is disabled. A host administrator can enable it in Chat settings.</p>}
      <label>Find saved channel<input type="search" value={filter} onChange={e => setFilter(e.target.value)} /></label>
      <label>Channel<select value={route.destinationId} onChange={e => {
        const d = channels.find(d => d.id === e.target.value);
        onChange({ ...route, destinationId: e.target.value, conversationPolicy: route.providerId === 'slack' ? 'thread' : d?.kind.includes('forum') ? 'topic' : 'flat' });
      }}><option value="">Choose channel</option>{channels.filter(d => d.id === route.destinationId || d.displayName.toLowerCase().includes(filter.toLowerCase())).map(d => <option key={d.id} value={d.id}>{d.displayName}{d.accessState === 'unavailable' ? ' · needs repair' : ''}</option>)}</select></label>
      {canManageConnections && <button type="button" className="chat-text-button" onClick={e => onChannelSetup(e.currentTarget)}>Discover or add channel</button>}
    </>}
    <label>Mode<select value={route.interactive ? 'interactive' : 'notifications'} onChange={e => onChange({ ...route, interactive: e.target.value === 'interactive' })}>
      <option value="interactive" disabled={notificationOnlySlice}>Interactive remote control</option><option value="notifications">Notification-only</option></select></label>
    <label>Content policy<select value={route.contentMode} onChange={e => onChange({ ...route, contentMode: e.target.value })}>
      <option value="metadata">Metadata only</option><option value="redacted">Redacted summary</option></select></label>
    {route.destinationId && <p className="chat-help">Channel access: {channels.find(d => d.id === route.destinationId)?.accessState ?? 'unavailable'}</p>}
    {!notificationOnlySlice && <details><summary>Delivery options</summary>
      <label>Conversation<select value={route.conversationPolicy} onChange={e => onChange({ ...route, conversationPolicy: e.target.value })}>
        {route.providerId === 'slack' ? <option value="thread">Parent thread</option> : <option value="topic">Parent topic</option>}<option value="flat">Channel messages</option></select></label>
      {['open-questions', 'workstream-stalled'].map(event => <label className="chat-checkline" key={event}>
        <input type="checkbox" checked={(route.eventKinds ?? ['open-questions', 'workstream-stalled']).includes(event)} onChange={e => {
          const current = route.eventKinds ?? ['open-questions', 'workstream-stalled']; onChange({ ...route, eventKinds: e.target.checked ? [...current, event] : current.filter(k => k !== event) });
        }} />{event}</label>)}
      <label className="chat-checkline"><input type="checkbox" checked={route.paused ?? false} onChange={e => onChange({ ...route, paused: e.target.checked })} />Pause delivery to this destination</label>
      <p className="chat-help">This pauses chat delivery only. Work continues running.</p>
    </details>}
  </section>;
}

export function ChatRowControl({ productId, spawnId, title, summary: initial, compact = false }: Props) {
  const { summary, error } = useChatSummary(productId, spawnId, initial, title);
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const descriptionId = useId();
  const state = error ? 'degraded' : summary?.state ?? 'loading';
  const destinations = summary?.destinations ?? [];
  const label = destinations.length > 1 ? `${destinations.length} destinations` : destinations.length === 1 ? `${destinations[0].providerId} / ${destinations[0].displayName}` : '';
  const stateLabel = state === 'needs-setup' ? 'Needs setup' : state === 'loading' ? 'Loading' : state[0].toUpperCase() + state.slice(1);
  const description = `${stateLabel}${label ? ` · ${label}` : ''}`;
  return <span className={`chat-row-control${compact ? ' chat-row-control-compact' : ''}`} onClick={e => e.stopPropagation()} onKeyDown={e => e.stopPropagation()}>
    <button ref={trigger} type="button" className={`${compact ? 'chat-action-button' : 'chat-row-button'} chat-state-${state}`} aria-label={`Chat for ${title}`} aria-haspopup="dialog" aria-describedby={compact ? descriptionId : undefined}
      title={error ? 'Chat status is unavailable. Open Chat to retry.' : `Chat · ${description}`} onClick={() => setOpen(true)}>
      <span className="chat-state-dot" aria-hidden="true" />
      {compact ? <><svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M3.5 2.5h9A1.5 1.5 0 0 1 14 4v6a1.5 1.5 0 0 1-1.5 1.5H7L3 14v-2.5A1.5 1.5 0 0 1 1.5 10V4A1.5 1.5 0 0 1 3.5 2.5Z" />
        <path d="M5 6.5h6M5 8.5h4" /></svg><span id={descriptionId} className="chat-action-description">{description}</span></> : <>
        Chat <span className="chat-row-state">{stateLabel}</span>{label && <span className="chat-row-destinations">{label}</span>}
      </>}
    </button>
    {open && createPortal(<ChatPanel productId={productId} spawnId={spawnId} title={title} onClose={() => { setOpen(false); trigger.current?.focus(); }} />, document.body)}
  </span>;
}

function ChatPanel({ productId, spawnId, title, onClose }: Props & { onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const panelBody = useRef<HTMLDivElement>(null);
  const editorArea = useRef<HTMLDivElement>(null);
  const editorReturn = useRef<{ trigger: HTMLButtonElement; scrollTop: number } | null>(null);
  const [saved, setSaved] = useState<ChatSetup | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [routes, setRoutes] = useState<ChatRouteDraft[]>([emptyChatRoute()]);
  const [connections, setConnections] = useState<ChatConnection[]>([]);
  const [destinations, setDestinations] = useState<ChatDestination[]>([]);
  const [management, setManagement] = useState<ChatManagement | null>(null);
  const [editor, setEditor] = useState<{ key: string; kind: 'connection' | 'channel' } | null>(null);
  const [source, setSource] = useState('');
  const [busy, setBusy] = useState(false);
  const [editorBusy, setEditorBusy] = useState(false);
  const [editorActions, setEditorActions] = useState<HTMLDivElement | null>(null);
  const close = () => { if (busy || editorBusy) return; dialog.current?.close(); onClose(); };
  const [error, setError] = useState('');
  const { rows } = useChatSummary(productId, spawnId);
  const refreshManagement = useCallback(() => { void chatApi.management().then(setManagement).catch(e => setError(e.message)); }, []);
  const loadCatalog = useCallback(async () => {
    const connections = await chatApi.connections(productId);
    const channels = (await Promise.all(connections.map(c => chatApi.destinations(productId, c.id)))).flat();
    setConnections(connections); setDestinations(channels); return { connections, channels };
  }, [productId]);
  function fromSaved(setup: ChatSetup, choices: ChatConnection[], channels: ChatDestination[]) {
    return setup.plugin.subscriptions.map(s => {
      const destination = channels.find(d => d.id === s.destinationId);
      return { ...s, key: crypto.randomUUID(), connectionId: destination?.connectionId ?? '', providerId: choices.find(c => c.id === destination?.connectionId)?.providerId ?? '', eventKinds: JSON.parse(s.eventKindsJson) as string[] };
    });
  }
  useEffect(() => {
    dialog.current?.showModal();
    let active = true;
    void Promise.all([chatApi.get(productId, spawnId), loadCatalog(), chatApi.management()]).then(([setup, catalog, management]) => {
      if (!active) return;
      setSaved(setup); setEnabled(setup.plugin.enabled); setManagement(management);
      const selected = fromSaved(setup, catalog.connections, catalog.channels); setRoutes(selected.length ? selected : [emptyChatRoute(!setup.notificationOnlySlice)]);
    }).catch(e => { if (active) setError(e.message); });
    return () => { active = false; };
  }, [productId, spawnId, loadCatalog]);
  function openEditor(key: string, kind: 'connection' | 'channel', trigger: HTMLButtonElement) {
    editorReturn.current = { trigger, scrollTop: panelBody.current?.scrollTop ?? 0 };
    setEditor({ key, kind });
  }
  useLayoutEffect(() => {
    const body = panelBody.current;
    if (!body) return;
    if (editor && editorArea.current) {
      // Put the active setup step in view and restore the draft when it closes.
      const area = editorArea.current;
      body.scrollTop += area.getBoundingClientRect().top - body.getBoundingClientRect().top;
      (area.querySelector<HTMLElement>('[data-chat-initial-focus]:enabled') ?? area.querySelector<HTMLElement>('input:enabled, select:enabled, textarea:enabled, button:enabled'))?.focus();
    } else if (!editor && editorReturn.current) {
      const { trigger, scrollTop } = editorReturn.current;
      body.scrollTop = scrollTop;
      if (trigger.isConnected) trigger.focus();
      editorReturn.current = null;
    }
  }, [editor]);
  const edited = routes.find(r => r.key === editor?.key);
  const update = (route: ChatRouteDraft) => setRoutes(old => old.map(r => r.key === route.key ? route : r));
  async function save() {
    if (!saved) return;
    setBusy(true); setError('');
    try {
      const next = await chatApi.save(productId, spawnId, { expectedRevision: saved.plugin.revision, enabled,
        destinations: routes.filter(r => r.destinationId).map(({ destinationId, conversationPolicy, interactive, contentMode, eventKinds, paused }) => ({ destinationId, conversationPolicy, interactive, contentMode, eventKinds, paused })),
        activateNewRun: saved.plugin.reason === 'new-run-needs-activation' });
      setSaved(next); await reloadChatSummaries(productId); close();
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function copy() {
    setBusy(true); setError('');
    try { const setup = await chatApi.get(productId, source); setRoutes(fromSaved(setup, connections, destinations)); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function reload() {
    setBusy(true);
    try {
      const [setup, catalog] = await Promise.all([chatApi.get(productId, spawnId), loadCatalog()]);
      setSaved(setup); setEnabled(setup.plugin.enabled); setEditor(null);
      const selected = fromSaved(setup, catalog.connections, catalog.channels); setRoutes(selected.length ? selected : [emptyChatRoute(!setup.notificationOnlySlice)]); setError('');
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  const complete = routes.length > 0 && routes.every(r => !!r.destinationId) && new Set(routes.map(r => r.destinationId)).size === routes.length;
  return <dialog ref={dialog} className="chat-panel swarm-ctrl" aria-labelledby="chat-panel-title" onClick={e => e.stopPropagation()}
    onKeyDown={e => e.stopPropagation()} onCancel={e => { e.preventDefault(); close(); }}>
    <div className="chat-panel-layout"><header className="chat-panel-header"><span className="chat-eyebrow">CHAT INTEGRATION</span>
      <h2 id="chat-panel-title">Remote control · {title}</h2><button type="button" className="chat-close" aria-label="Close remote control" disabled={busy || editorBusy} onClick={close}><span aria-hidden="true">×</span> Close</button>
      <p className="chat-help">One parent conversation per destination covers every current and future child.</p></header>
    <div ref={panelBody} className="chat-panel-body">
      {!saved && !error && <p role="status">Loading saved settings…</p>}
      {saved && <>
        <div hidden={!!editor}>
          {saved.hostReason && <p className="chat-notice">{chatReason(saved.hostReason)} Configuration can still be saved.</p>}
          {!saved.canManage && <p className="chat-notice">A product owner can configure this row. Host administration is a separate permission.</p>}
          <fieldset disabled={busy || !saved.canManage || !!editor}>
            <label className="chat-toggle-line"><span><strong>Enable chat remote control</strong><small>Chat settings leave the story's work state unchanged.</small></span>
              <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /></label>
            {enabled && <>
              {routes.map(route => <ChatRouteFields key={route.key} route={route} connections={connections} destinations={destinations} canManageConnections={saved.canManageConnections} notificationOnlySlice={saved.notificationOnlySlice}
                onChange={update} onSetup={trigger => openEditor(route.key, 'connection', trigger)} onChannelSetup={trigger => openEditor(route.key, 'channel', trigger)}
                onRemove={() => setRoutes(old => old.filter(r => r.key !== route.key))} />)}
              <button type="button" disabled={saved.notificationOnlySlice && routes.length > 0} onClick={() => setRoutes(old => [...old, emptyChatRoute(!saved.notificationOnlySlice)])}>Add provider/channel</button>
              <details><summary>Copy destinations from another row</summary><label>Source row<select value={source} onChange={e => setSource(e.target.value)}>
                <option value="">Choose source row</option>{rows.filter(r => r.spawnId !== spawnId && r.summary.destinations.length).map(r => <option key={r.spawnId} value={r.spawnId}>{r.title}</option>)}</select></label>
                <button type="button" disabled={!source} onClick={() => void copy()}>Copy into draft</button><p className="chat-help">Review the copied destinations, then save to enable them for this row.</p></details>
            </>}
          </fieldset>
        </div>
        {edited && editor && <div ref={editorArea}>
          {editor.kind === 'connection' && management && <ChatConnectionEditor providerId={edited.providerId} productId={productId} management={management} actionsTarget={editorActions} onBusyChange={setEditorBusy}
            onCancel={() => setEditor(null)} onSaved={id => { void loadCatalog().then(() => { update({ ...edited, connectionId: id, destinationId: '' }); setEditor(null); }).catch(e => setError(e.message)); }} />}
          {editor.kind === 'channel' && connections.find(c => c.id === edited.connectionId) && <ChatDestinationSetup connection={connections.find(c => c.id === edited.connectionId)!} actionsTarget={editorActions} onBusyChange={setEditorBusy}
            onCancel={() => setEditor(null)} onSaved={d => { setDestinations(old => [...old, d]); update({ ...edited, destinationId: d.id, conversationPolicy: edited.providerId === 'slack' ? 'thread' : d.kind.includes('forum') ? 'topic' : 'flat' }); setEditor(null); }} />}
        </div>}
        <div hidden={!!editor}>
          {enabled && routes.some(r => r.contentMode === 'redacted') && !saved.redactedContentAllowed && <p className="chat-notice">This product currently permits metadata only. A host administrator must opt in to redacted content and refresh the host before question text and answer controls are available.</p>}
          {enabled && <ChatPreview productId={productId} contentMode={routes.some(r => r.contentMode === 'redacted') ? 'redacted' : 'metadata'} />}
          {management && enabled && routes.some(r => r.interactive) && <ChatIdentityLink connections={connections.filter(c => routes.some(r => r.connectionId === c.id && r.interactive))} management={management} onRefresh={refreshManagement} />}
          <ChatDeliveryHistory productId={productId} spawnId={spawnId} canManage={saved.canManage} />
          <p className="chat-help">Retiring a subscription stops its routing and controls. Messages already held by Telegram or Slack remain in that provider.</p>
        </div>
      </>}
      {error && <><p className="chat-error" role="alert">{error}</p><button type="button" disabled={busy} onClick={() => void reload()}>Reload saved settings</button></>}
    </div><footer className="chat-panel-footer">
      {editor ? <div ref={setEditorActions} className="chat-editor-footer" /> : <><button type="button" disabled={busy} onClick={close}>Cancel</button>
        <button type="button" className="primary" disabled={!saved?.canManage || busy || enabled && !complete} onClick={() => void save()}>{busy ? 'Saving…' : enabled ? 'Save and enable' : 'Save'}</button></>}
    </footer></div>
  </dialog>;
}