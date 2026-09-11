import { useEffect, useState } from 'react';
import { chatRequest, connectionPath, type ChatConnection, type ChatLinkChallenge, type ChatManagement } from './chatApi';

export function ChatIdentityLink({ connections, management, onRefresh }: { connections: ChatConnection[]; management: ChatManagement; onRefresh: () => void }) {
  const [connection, setConnection] = useState('');
  const [tenant, setTenant] = useState('');
  const [sender, setSender] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [challenge, setChallenge] = useState<ChatLinkChallenge | null>(null);
  const [expiresAt, setExpiresAt] = useState(0);
  const [now, setNow] = useState(Date.now());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    if (connection && !connections.some(c => c.id === connection && c.enabled)) {
      setConnection(''); setTenant(''); setChallenge(null); setConfirmed(false);
    }
  }, [connection, connections]);
  useEffect(() => { if (!challenge) return; const timer = setInterval(() => { setNow(Date.now()); onRefresh(); }, 5000); return () => clearInterval(timer); }, [challenge, onRefresh]);
  async function link() {
    setBusy(true); setError('');
    try { const c = await chatRequest<ChatLinkChallenge>(connectionPath(connection) + '/link-challenge', 'POST', { externalTenantId: tenant, externalUserId: sender }); setChallenge(c); setExpiresAt(Date.now() + c.expiresInSeconds * 1000); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  async function revoke() {
    setBusy(true); setError('');
    try { await chatRequest('/api/chat/operator-links/self', 'DELETE'); setChallenge(null); onRefresh(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <section className="chat-card" aria-label="Operator identity"><h3>Your chat identity</h3>
    <p className="chat-help">Link your own chat account before using work actions. Your Forge product permissions still apply. Notifications work without a link.</p>
    <ul>{connections.filter(c => c.enabled).map(c => <li key={c.id}>{c.displayName}: {management.links.some(l => l.providerId === c.providerId && l.externalTenantId === c.installationId) ? 'Identity verified for this installation' : 'Identity not linked to this installation'}</li>)}</ul>
    {management.links.length ? <><ul>{management.links.map(l => <li key={`${l.providerId}:${l.externalTenantId}:${l.externalUserId}`}>{l.providerId} · {l.externalTenantId} · {l.externalUserId} — linked</li>)}</ul>
      <button type="button" disabled={busy} onClick={() => void revoke()}>Revoke my chat links</button></> : <p className="chat-notice">Not linked</p>}
    <details><summary>Link or re-link my account</summary><fieldset disabled={busy}>
      <label>Identity connection<select value={connection} onChange={e => { setConnection(e.target.value); setTenant(connections.find(c => c.id === e.target.value)?.installationId ?? ""); setChallenge(null); setConfirmed(false); }}>
        <option value="">Choose connection</option>{connections.filter(c => c.enabled).map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}</select></label>
      <label>Workspace or bot ID<input value={tenant} onChange={e => { setTenant(e.target.value); setChallenge(null); setConfirmed(false); }} /></label>
      <label>My chat user ID<input value={sender} onChange={e => { setSender(e.target.value); setChallenge(null); setConfirmed(false); }} /></label>
      <label className="chat-checkline"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />I confirm this is my own chat account</label>
      <button type="button" disabled={!confirmed || !connection || !tenant || !sender} onClick={() => void link()}>Create link challenge</button>
    </fieldset>
    {challenge && (now >= expiresAt ? <p className="chat-notice">Challenge expired. Create a new challenge to continue.</p> : <div className="chat-preview">
      <p>Complete within two minutes from that chat account.</p>
      {challenge.deepLink ? <a href={challenge.deepLink} target="_blank" rel="noreferrer">Confirm in Telegram</a> : <code>{challenge.channelCommand ?? `/link ${challenge.token}`}</code>}
      <button type="button" onClick={onRefresh}>Check link status</button>
    </div>)}</details>
    {error && <p className="chat-error" role="alert">{error}</p>}
  </section>;
}