import { useEffect, useId, useRef, useState } from 'react';
import { chatRequest, connectionPath, type ChatConnection, type ChatDestination, type ChatLinkChallenge, type ChatManagement } from './chatApi';
import { ChatSlackIdentityConfirmation } from './ChatSlackIdentityConfirmation';

export function ChatIdentityLink({ connections, management, onRefresh }: { connections: ChatConnection[]; management: ChatManagement; onRefresh: () => void }) {
  const [connection, setConnection] = useState('');
  const [tenant, setTenant] = useState('');
  const [sender, setSender] = useState('');
  const [restoredSender, setRestoredSender] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [challenge, setChallenge] = useState<ChatLinkChallenge | null>(null);
  const [expiresAt, setExpiresAt] = useState(0);
  const [now, setNow] = useState(Date.now());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [destinations, setDestinations] = useState<ChatDestination[]>([]);
  const [destinationId, setDestinationId] = useState('');
  const [loadingDestinations, setLoadingDestinations] = useState(false);
  const [slackDialog, setSlackDialog] = useState(false);
  const senderHelpId = useId();
  const popup = useRef<Window | null>(null);
  const requestVersion = useRef(0);
  const selected = connections.find(c => c.id === connection && c.enabled);
  const telegram = selected?.providerId === 'telegram';
  const slack = selected?.providerId === 'slack';
  const slackAvailable = !!management.slackLinkConfirmationAvailable;
  const destination = destinations.find(d => d.id === destinationId);
  const slackUrl = destination ? `https://slack.com/app_redirect?team=${encodeURIComponent(tenant)}&channel=${encodeURIComponent(destination.channelId)}` : '';
  const senderId = sender.trim();
  const senderValid = telegram ? /^[1-9][0-9]{0,15}$/.test(senderId) && Number(senderId) <= 4503599627370495 : /^[UW][A-Z0-9]{1,127}$/.test(senderId);
  const expired = !!challenge && now >= expiresAt;
  const linked = management.links.some(l => l.providerId === selected?.providerId && l.externalTenantId === tenant && l.externalUserId === senderId);
  const canLink = management.interactiveAvailable && !!selected && !!tenant && senderValid && confirmed && !busy && !linked
    && (!slack || slackAvailable && !!destination && !loadingDestinations);
  function resetChallenge() {
    requestVersion.current++; popup.current?.close(); popup.current = null;
    setChallenge(null); setConfirmed(false); setError(''); setNotice(''); setBusy(false); setSlackDialog(false);
  }
  useEffect(() => () => { requestVersion.current++; popup.current?.close(); }, []);
  useEffect(() => {
    let active = true;
    setDestinations([]); setDestinationId('');
    if (!slack || !slackAvailable) { setLoadingDestinations(false); return; }
    setLoadingDestinations(true);
    chatRequest<ChatDestination[]>(connectionPath(connection) + '/link-destinations').then(items => {
      if (!active) return;
      setDestinations(items); setDestinationId(items.length === 1 ? items[0].id : '');
    }).catch(e => { if (active) setError((e as Error).message); }).finally(() => { if (active) setLoadingDestinations(false); });
    return () => { active = false; };
  }, [connection, slack, slackAvailable]);
  useEffect(() => {
    if (connection && !connections.some(c => c.id === connection && c.enabled)) {
      setConnection(''); setTenant(''); resetChallenge();
    }
  }, [connection, connections]);
  useEffect(() => {
    if (!challenge || expired) return;
    const timer = setInterval(() => { setNow(Date.now()); onRefresh(); }, 2500);
    window.addEventListener('focus', onRefresh);
    return () => { clearInterval(timer); window.removeEventListener('focus', onRefresh); };
  }, [challenge, expired, onRefresh]);
  useEffect(() => {
    if (!linked || !challenge) return;
    popup.current?.close(); popup.current = null; setChallenge(null); setSlackDialog(false); setNotice('Your chat account is linked.');
  }, [linked, challenge]);
  async function link() {
    if (!canLink) return;
    const version = ++requestVersion.current;
    setBusy(true); setError(''); setNotice(''); setChallenge(null);
    setSlackDialog(slack);
    let windowRef: Window | null = null;
    try {
      if (slack && popup.current && !popup.current.closed) { windowRef = popup.current; windowRef.focus(); }
      else { popup.current?.close(); windowRef = window.open(telegram ? 'about:blank' : slackUrl, '_blank', 'popup,width=850,height=780'); }
      if (windowRef) { windowRef.opener = null; if (telegram) windowRef.document.title = 'Link Telegram account · Forge'; }
    } catch { /* The confirmation dialog keeps a direct link available. */ }
    popup.current = windowRef;
    try {
      const c = await chatRequest<ChatLinkChallenge>(connectionPath(connection) + (slack ? '/link-confirmation' : '/link-challenge'), 'POST',
        slack ? { destinationId, externalUserId: senderId } : { externalTenantId: tenant, externalUserId: senderId });
      if (version !== requestVersion.current) { windowRef?.close(); return; }
      if (!c.deepLink) throw new Error(`${telegram ? 'Telegram' : 'Slack'} could not open account confirmation. Check the connection and try again.`);
      const started = Date.now(); setNow(started); setChallenge(c); setExpiresAt(started + c.expiresInSeconds * 1000);
      if (telegram && windowRef) windowRef.location.replace(c.deepLink);
    } catch (e) { windowRef?.close(); if (version === requestVersion.current) setError((e as Error).message); }
    finally { if (version === requestVersion.current) setBusy(false); }
  }
  async function cancelSlack() {
    if (busy) return;
    setBusy(true); setError('');
    try { await chatRequest(connectionPath(connection) + '/link-confirmation', 'DELETE'); resetChallenge(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function revoke() {
    setBusy(true); setError('');
    try { await chatRequest('/api/chat/operator-links/self', 'DELETE'); resetChallenge(); setSender(''); setRestoredSender(false); onRefresh(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <section className="chat-card chat-identity" aria-label="Operator identity"><header className="chat-section-heading"><div><span className="chat-eyebrow">ACCOUNT ACCESS</span><h3>Your chat identity</h3></div></header>
    <p className="chat-help">Link your own chat account before using work actions. Your Forge product permissions still apply. Notifications work without a link.</p>
    <ul className="chat-identity-connections">{connections.filter(c => c.enabled).map(c => {
      const verified = management.links.some(l => l.providerId === c.providerId && l.externalTenantId === c.installationId);
      return <li key={c.id}><span title={c.displayName}>{c.displayName}</span><span className="chat-identity-state" data-linked={verified} title={verified ? 'Identity verified for this installation' : 'Identity not linked to this installation'}>{verified ? 'Linked' : 'Needs linking'}</span></li>;
    })}</ul>
    {management.links.length ? <details className="chat-identity-records"><summary>Verified account details · {management.links.length}</summary>
      <ul>{management.links.map(l => <li key={`${l.providerId}:${l.externalTenantId}:${l.externalUserId}`}>{l.providerId} · {l.externalTenantId} · {l.externalUserId} — linked</li>)}</ul>
      <button type="button" disabled={busy} onClick={() => void revoke()}>Revoke my chat links</button></details> : <p className="chat-notice">Not linked</p>}
    <details><summary>Link or re-link my account</summary>
    {!management.interactiveAvailable && <p className="chat-notice" role="status">Account linking is waiting for chat activation on this host. Activate the plugin chat engine before confirming your account.</p>}
    <fieldset disabled={busy}>
      <label>Identity connection<select value={connection} onChange={e => {
        const next = connections.find(c => c.id === e.target.value);
        const known = [...new Set(management.links.filter(l => l.providerId === next?.providerId &&
          (next?.providerId === 'telegram' || l.externalTenantId === next?.installationId)).map(l => l.externalUserId))];
        const previous = next?.providerId === 'slack' && known.length === 0
          ? [...new Set((management.recentLinkTargets ?? []).filter(t => t.connectionId === next.id && t.externalTenantId === next.installationId
            && /^[UW][A-Z0-9]{1,127}$/.test(t.externalUserId)).map(t => t.externalUserId))] : [];
        setConnection(e.target.value); setTenant(next?.installationId ?? '');
        setSender(known.length === 1 ? known[0] : previous.length === 1 ? previous[0] : '');
        setRestoredSender(previous.length === 1); resetChallenge();
      }}>
        <option value="">Choose connection</option>{connections.filter(c => c.enabled).map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}</select></label>
      <label>Workspace or bot ID<input value={tenant} readOnly /></label>
      <label>My chat user ID<input value={sender} inputMode={telegram ? 'numeric' : 'text'} autoComplete="off"
        aria-describedby={telegram ? senderHelpId : undefined} aria-invalid={telegram && !!senderId && !senderValid}
        onChange={e => { setSender(e.target.value); setRestoredSender(false); resetChallenge(); }} /></label>
      {telegram && <p id={senderHelpId} className={senderId && !senderValid ? 'chat-error' : 'chat-help'}>
        Use your personal account’s numeric Telegram ID. A username or bot name will not work. A previously verified account ID is filled in automatically.
      </p>}
      {slack && <>
        {restoredSender && <p className="chat-help">Filled from your previous attempt. Check this is still your account, then approve the link in Slack.</p>}
        <p className="chat-help">Forge sends a private confirmation to this account. In Slack, your profile’s menu has Copy member ID.</p>
        {!slackAvailable ? <p className="chat-notice" role="status">Automatic Slack confirmation needs the updated Forge host. A host restart is pending.</p>
          : <label>Confirmation channel<select value={destinationId} disabled={loadingDestinations} onChange={e => { setDestinationId(e.target.value); resetChallenge(); }}>
            <option value="">{loadingDestinations ? 'Loading channels…' : 'Choose a channel you have joined'}</option>
            {destinations.map(d => <option key={d.id} value={d.id}>{d.displayName}</option>)}</select></label>}
        {slackAvailable && !loadingDestinations && !destinations.length && <p className="chat-notice">Select this connection above and add a channel before linking your account.</p>}
      </>}
      {linked && <p className="chat-notice" role="status">This account is already linked to the selected connection.</p>}
      <label className="chat-checkline"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />I confirm this is my own chat account</label>
      <button type="button" className="primary" disabled={!canLink} onClick={() => void link()}>{busy ? 'Opening confirmation…' : telegram ? 'Link my Telegram account' : 'Link my Slack account'}</button>
    </fieldset>
    {telegram && challenge && (expired ? <p className="chat-notice">Confirmation expired. Start again to open a fresh confirmation.</p> : <div className="chat-preview">
      <p>Confirm with Start in Telegram. Forge checks the result automatically; return here when confirmed.</p>
      {challenge.deepLink && <a href={challenge.deepLink} target="_blank" rel="noopener noreferrer">Open Telegram confirmation</a>}
      <button type="button" onClick={onRefresh}>Check link status</button>
    </div>)}</details>
    {slackDialog && <ChatSlackIdentityConfirmation channelName={destination?.displayName ?? ''} url={slackUrl}
      requested={!!challenge} expired={expired} busy={busy} error={error} onRetry={() => void link()} onClose={() => void cancelSlack()} />}
    {notice && <p className="chat-notice" role="status">{notice}</p>}
    {error && !slackDialog && <p className="chat-error" role="alert">{error}</p>}
  </section>;
}