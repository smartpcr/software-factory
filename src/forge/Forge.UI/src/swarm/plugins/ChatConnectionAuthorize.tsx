import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { chatReason, chatRequest } from './chatApi';
import type { ChatConnectionEditorProps } from './ChatConnectionEditor';
import { ChatTelegramSetup } from './ChatTelegramSetup';
import { ChatSlackApproval } from './ChatSlackApproval';

interface Availability { providerId: string; available: boolean; reason: string | null; canConfigure?: boolean }
interface Authorization {
  id: string; providerId: string; status: 'waiting' | 'approved' | 'saved' | 'failed' | 'cancelled'; expiresAt: string;
  authorizationUrl: string | null; accountName: string | null; installationId: string | null; reason: string | null; connectionId: string | null;
  setupMode?: string | null; approvalRequest?: string | null; appId?: string | null;
}
const path = '/api/chat/authorization';

export function ChatConnectionAuthorize({ providerId, productId, management, onSaved, onCancel, actionsTarget, onBusyChange, onManual }: ChatConnectionEditorProps & { onManual: () => void }) {
  const providerName = providerId === 'slack' ? 'Slack' : 'Telegram';
  const requirementId = useId();
  const [availability, setAvailability] = useState<Availability | null>(null);
  const [availabilityError, setAvailabilityError] = useState('');
  const [flow, setFlow] = useState<Authorization | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [name, setName] = useState('');
  const [products, setProducts] = useState<string[]>(productId ? [productId] : []);
  const [includePrivateChannels, setIncludePrivateChannels] = useState(false);
  const [managerName, setManagerName] = useState('');
  const [showSlackApproval, setShowSlackApproval] = useState(false);
  const popup = useRef<Window | null>(null);
  const active = useRef(true);
  const current = useRef<Authorization | null>(null);
  const nameInput = useRef<HTMLInputElement>(null);
  const needsTelegramSetup = providerId === 'telegram' && availability?.canConfigure === true && !availability.available;
  const needsSlackSetup = providerId === 'slack' && availability?.canConfigure === true && !availability.available;
  const canStart = availability?.available || needsSlackSetup;
  const unavailableReason = availability && !availability.available && !needsSlackSetup
    ? availability.reason === 'authorization-registration-required'
      ? providerId === 'telegram' ? 'Unavailable until a Telegram manager bot is configured on this host.'
        : 'Unavailable until the Slack app and HTTPS return address are configured on this host.'
      : chatReason(availability.reason)
    : '';
  const remember = (value: Authorization) => { current.current = value; setFlow(value); };

  useEffect(() => {
    active.current = true;
    void chatRequest<Availability[]>(path).then(items => {
      if (active.current) setAvailability(items.find(a => a.providerId === providerId) ?? { providerId, available: false, reason: 'authorization-registration-required' });
    }).catch(() => { if (active.current) setAvailabilityError('Automatic authorization is unavailable on this host. Update the Forge host to enable it, or use an existing bot below.'); });
    return () => {
      active.current = false;
      popup.current?.close();
      const pending = current.current;
      if (pending && pending.status !== 'saved') void chatRequest(`${path}/${pending.id}`, 'DELETE').catch(() => {});
    };
  }, [providerId]);
  useEffect(() => { onBusyChange?.(busy); }, [busy, onBusyChange]);
  useEffect(() => () => onBusyChange?.(false), [onBusyChange]);

  useEffect(() => {
    if (flow?.status !== 'waiting' || flow.setupMode === 'slack-cli') return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const result = await chatRequest<Authorization>(`${path}/${flow.id}/poll`, 'POST');
        if (disposed) return;
        current.current = result; setFlow(result); setError('');
        if (result.status === 'approved') { setName(result.accountName ?? providerName); popup.current?.close(); }
        if (result.status === 'waiting') timer = setTimeout(() => void poll(), 2500);
      } catch (e) {
        if (disposed) return;
        setError((e as Error).message);
        // Stop on errors so a stale session does not loop forever. Retry is explicit.
      }
    };
    timer = setTimeout(() => void poll(), 1200);
    return () => { disposed = true; clearTimeout(timer); };
  }, [flow?.id, flow?.status, flow?.setupMode, providerName]);
  useEffect(() => { if (flow?.status === 'approved') nameInput.current?.focus(); }, [flow?.status]);

  async function start(managerToken?: string) {
    setBusy(true); setError('');
    popup.current?.close();
    // Open during the click to satisfy popup blockers; the provider cannot control the Forge window.
    const windowRef = needsSlackSetup ? null : window.open('about:blank', '_blank', 'popup,width=650,height=780');
    if (windowRef) { windowRef.opener = null; windowRef.document.title = `Connect ${providerName} · Forge`; }
    popup.current = windowRef;
    try {
      if (managerToken) {
        const manager = await chatRequest<{ username: string }>(`${path}/telegram/manager`, 'POST', { token: managerToken });
        if (!active.current) { windowRef?.close(); return; }
        setManagerName(manager.username);
        setAvailability({ providerId, available: true, reason: null, canConfigure: false });
      }
      if (current.current && current.current.status !== 'saved') await chatRequest(`${path}/${current.current.id}`, 'DELETE').catch(() => {});
      const result = await chatRequest<Authorization>(needsSlackSetup ? `${path}/slack/setup` : path, 'POST', { providerId, includePrivateChannels });
      if (!active.current) { windowRef?.close(); void chatRequest(`${path}/${result.id}`, 'DELETE').catch(() => {}); return; }
      remember(result);
      if (result.setupMode === 'slack-cli') setShowSlackApproval(true);
      if (result.authorizationUrl && windowRef) windowRef.location.replace(result.authorizationUrl);
    } catch (e) { windowRef?.close(); if (active.current) setError((e as Error).message); }
    finally { if (active.current) setBusy(false); }
  }
  async function approveSlack(code: string) {
    if (!flow || busy) return;
    setBusy(true); setError('');
    try {
      const result = await chatRequest<Authorization>(`${path}/${flow.id}/slack/approve`, 'POST', { code });
      if (!active.current) return;
      remember(result); setShowSlackApproval(false);
      if (result.status === 'approved') setName(result.accountName ?? 'Slack');
    } catch (e) { if (active.current) { setShowSlackApproval(false); setError((e as Error).message); } }
    finally { if (active.current) setBusy(false); }
  }
  async function save() {
    if (!flow) return;
    setBusy(true); setError('');
    try {
      const result = await chatRequest<Authorization>(`${path}/${flow.id}/save`, 'POST', { displayName: name, productIds: products });
      if (!active.current) return;
      remember(result);
      if (result.connectionId) onSaved(result.connectionId);
    } catch (e) { if (active.current) setError((e as Error).message); }
    finally { if (active.current) setBusy(false); }
  }
  const actions = <div className="chat-actions chat-editor-actions"><button type="button" disabled={busy} onClick={onCancel}>Cancel connection setup</button>
    {flow?.status === 'approved' && <button type="button" className="primary" disabled={busy || !name.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save connection'}</button>}</div>;

  return <section className="chat-card chat-connection-editor chat-authorization" aria-label="Connection setup">
    <header className="chat-editor-heading"><div><span className="chat-eyebrow">{providerName.toUpperCase()} CONNECTION</span><h3>Set up connection</h3></div>
      {!actionsTarget && <button type="button" aria-label="Close connection setup" disabled={busy} onClick={onCancel}>Close</button>}</header>
    <p className="chat-authorization-intro">{needsTelegramSetup ? 'Set up Telegram once below. Forge then creates each connection through Telegram’s approval screen.'
      : providerId === 'slack' ? 'Choose your Slack workspace and approve Forge. Your workspace details and credentials are filled in automatically.'
      : 'Approve a new Forge bot in Telegram. Forge retrieves and stores its credentials for you.'}</p>
    <ol className="chat-authorization-steps" aria-label="Connection steps">
      {needsTelegramSetup && <li aria-current="step">Set up Telegram once</li>}
      <li aria-current={!needsTelegramSetup && (!flow || flow.status === 'waiting') ? 'step' : undefined}>Approve in {providerName}</li>
      <li aria-current={flow?.status === 'approved' ? 'step' : undefined}>Review and save</li>
      <li>Choose a channel</li>
    </ol>
    {availabilityError && <p className="chat-warning" role="status">{availabilityError}</p>}
    {needsTelegramSetup && !flow && <ChatTelegramSetup busy={busy} onContinue={token => void start(token)} />}
    {managerName && <div className="chat-authorization-notice" role="status"><strong>Telegram setup complete · @{managerName}</strong>
      <p>The manager credential is verified and saved encrypted. Future connections on this host can go straight to Telegram approval.</p></div>}
    {needsSlackSetup && !flow && <p className="chat-help">Forge can create a dedicated Slack app for your workspace. Approve Slack’s developer permission prompt, then Forge configures the app and retrieves both credentials. No existing app registration is required.</p>}
    {availability && !availability.available && !needsTelegramSetup && !needsSlackSetup && <div className="chat-authorization-notice" role="status"><strong>One-time host setup needed</strong>
      <p>{chatReason(availability.reason)}</p>
      <p>{providerId === 'slack' ? 'A Forge host administrator must register the shared Slack app and its HTTPS return address once. After that, each connection uses Slack’s approval screen.'
        : 'A Forge host administrator must register one dedicated manager bot with Bot Management Mode enabled. After that, Telegram can create each connection with an approval prompt.'}</p>
    </div>}
    {!flow && !needsTelegramSetup && <>
      {providerId === 'slack' && <label className="chat-checkline"><input type="checkbox" checked={includePrivateChannels} disabled={busy} onChange={e => setIncludePrivateChannels(e.target.checked)} />Include private channels</label>}
      <button data-chat-initial-focus type="button" className="primary chat-authorization-connect" disabled={busy || !canStart}
        aria-describedby={unavailableReason ? requirementId : undefined} title={unavailableReason || undefined} onClick={() => void start()}>
        {busy ? 'Opening authorization…' : `Connect with ${providerName}`}</button>
      {unavailableReason && <p id={requirementId} className="chat-connect-requirement" role="status">{unavailableReason}</p>}
      {!availability && !availabilityError && <p role="status">Checking authorization setup…</p>}
    </>}
    {flow?.status === 'waiting' && flow.setupMode === 'slack-cli' && <div className="chat-authorization-notice" role="status">
      <strong>Slack approval needed</strong><p>Complete Slack’s permission prompt so Forge can configure the connection.</p>
      <button type="button" className="primary" disabled={busy} onClick={() => setShowSlackApproval(true)}>Open Slack approval prompt</button>
      <button type="button" disabled={busy} onClick={() => void start()}>Start authorization again</button>
    </div>}
    {flow?.status === 'waiting' && flow.setupMode !== 'slack-cli' && <div className="chat-authorization-notice" role="status"><strong>Waiting for your approval in {providerName}</strong>
      <p>{providerId === 'slack' ? 'Sign in if asked, choose your workspace, then select Allow. Return here to finish.'
        : 'Sign in to Telegram if asked and confirm bot creation. Keep the suggested bot username so Forge can match your approval; you can edit the display name.'}</p>
      <p>This request expires at {new Date(flow.expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.</p>
      {flow.authorizationUrl && <a href={flow.authorizationUrl} target="_blank" rel="noopener noreferrer">Open {providerName} authorization</a>}
      <p className="chat-help">If no window opened, use the link above. Closing this setup cancels the Forge request; an app or bot you already approved stays in {providerName}.</p>
    </div>}
    {flow?.status === 'approved' && <fieldset disabled={busy}>
      <div className="chat-authorization-notice" role="status"><strong>{providerName} approved · {flow.accountName}</strong><p>{providerId === 'slack' ? 'Workspace' : 'Bot'} {flow.installationId}. Credentials are stored on the Forge host when you save. You can close the provider window.</p></div>
      <label>Connection name<input ref={nameInput} value={name} onChange={e => setName(e.target.value)} /></label>
      <details className="chat-section" open><summary>Products allowed to use this connection · {products.length} selected</summary><div className="chat-section-content">
        {management.products.map(p => <label className="chat-checkline" key={p.id}><input type="checkbox" checked={products.includes(p.id)} onChange={e => setProducts(ids => e.target.checked ? [...ids, p.id] : ids.filter(id => id !== p.id))} />{p.name}</label>)}
      </div></details>
      <p className="chat-help">After saving, choose a channel for your story. {providerId === 'slack' ? 'Invite the app to the channel so Forge can access it.' : 'Add the bot to your group or channel before selecting it.'}</p>
    </fieldset>}
    {(flow?.status === 'failed' || flow?.status === 'cancelled') && <p className="chat-error" role="alert">{chatReason(flow.reason ?? 'authorization-denied')}</p>}
    {flow?.status === 'failed' && flow.appId && <p className="chat-help">The app was created before setup stopped. It remains in Slack. <a href={`https://api.slack.com/apps/${encodeURIComponent(flow.appId)}`} target="_blank" rel="noopener noreferrer">Review the created Slack app</a> before starting again.</p>}
    {error && <p className="chat-error" role="alert">{error}</p>}
    {!needsTelegramSetup && !(flow?.status === 'waiting' && flow.setupMode === 'slack-cli') && (error || flow?.status === 'failed' || flow?.status === 'cancelled') && <button type="button" disabled={busy || !canStart} onClick={() => void start()}>Start authorization again</button>}
    {!flow && <details className="chat-section"><summary>Advanced · use existing credentials</summary><div className="chat-section-content">
      <p className="chat-help">For an existing Slack app or Telegram bot, you can enter its credentials and connection details.</p>
      <button type="button" disabled={busy} onClick={onManual}>Set up manually</button>
    </div></details>}
    {actionsTarget ? createPortal(actions, actionsTarget) : actions}
    {showSlackApproval && flow?.status === 'waiting' && flow.approvalRequest && <ChatSlackApproval key={flow.id} approvalRequest={flow.approvalRequest}
      expiresAt={flow.expiresAt} busy={busy} error={error} onApprove={code => void approveSlack(code)}
      onRestart={() => void start()} onClose={() => setShowSlackApproval(false)} />}
  </section>;
}