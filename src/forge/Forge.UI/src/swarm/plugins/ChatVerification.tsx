import { useCallback, useEffect, useState } from 'react';
import { chatApi, chatReason, chatRequest, connectionPath, rowPath, type ChatCheck, type ChatDestination, type ChatHealth, type ChatHistory } from './chatApi';

export function ChatPreview({ productId, contentMode }: { productId?: string; contentMode: string }) {
  const [text, setText] = useState('An operator response is needed. token=example-secret C:\\private\\build.log');
  const [preview, setPreview] = useState('');
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    const timer = setTimeout(() => { void chatRequest<{ text: string }>('/api/chat/preview', 'POST', { productId: productId ?? null, contentMode, text })
      .then(p => { if (active) { setPreview(p.text); setError(''); } }).catch(e => { if (active) setError(e.message); }); }, 200);
    return () => { active = false; clearTimeout(timer); };
  }, [productId, contentMode, text]);
  return <details className="chat-preview"><summary>Redacted message preview</summary>
    <label>Sample summary<textarea maxLength={2000} value={text} onChange={e => setText(e.target.value)} /></label>
    <pre>{preview}</pre><p className="chat-help">Preview only. Nothing is sent to a provider.</p>{error && <p role="alert">{error}</p>}
  </details>;
}

export function ChatDeliveryHistory({ productId, spawnId, canManage, history }: { productId?: string; spawnId?: string; canManage: boolean; history?: ChatHistory }) {
  const [saved, setSaved] = useState<ChatHistory | undefined>(history);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const load = useCallback(async () => {
    if (productId && spawnId) setSaved(await chatRequest<ChatHistory>(rowPath(productId, spawnId) + '/history', 'GET', undefined, productId));
    else if (history) setSaved(history);
  }, [productId, spawnId, history]);
  useEffect(() => { void load().catch(e => setError(e.message)); const timer = setInterval(() => { void load().catch(e => setError(e.message)); }, 10000); return () => clearInterval(timer); }, [load]);
  async function retry(id: string) {
    setBusy(true); setError('');
    try { await chatRequest(`/api/chat/deliveries/${encodeURIComponent(id)}/retry`, 'POST'); await load(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <details className="chat-history"><summary>Delivery history{saved ? ` · ${saved.deliveries.length}` : ''}</summary>
    {saved && <><p className="chat-help">Unrouted: {Object.values(saved.unrouted).reduce((a, b) => a + b, 0)} {Object.entries(saved.unrouted).map(([reason, count]) => `${reason}: ${count}`).join(' · ')}</p>
      {saved.deliveries.length === 0 && <p className="chat-help">No deliveries yet.</p>}
      <ul>{saved.deliveries.map(d => <li key={d.id}><strong>{d.state}</strong> · {d.connectionId} · {d.attempts} attempts
        {d.lastError && <p className="chat-notice">{chatReason(d.lastError)}</p>}
        {['retry', 'pending'].includes(d.state) && <p>Next attempt: {new Date(d.nextAttemptAt).toLocaleString()}</p>}
        {d.state === 'uncertain' && <p className="chat-notice">Outcome unknown. Check the channel before requesting a fresh notification.</p>}
        {canManage && d.state === 'failed' && <button type="button" disabled={busy} onClick={() => void retry(d.id)}>Retry delivery</button>}
      </li>)}</ul></>}
    {error && <p className="chat-error" role="alert">{error}</p>}
  </details>;
}

export function ChatConnectionVerification({ connectionId, destination, onChanged }: { connectionId: string; destination: ChatDestination; onChanged: () => void }) {
  const [health, setHealth] = useState<ChatHealth | null>(null);
  const [checks, setChecks] = useState<ChatCheck[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const path = connectionPath(connectionId) + `/destinations/${encodeURIComponent(destination.id)}/checks`;
  const load = useCallback(async () => {
    const [health, checks] = await Promise.all([chatApi.health(connectionId), chatRequest<ChatCheck[]>(path)]);
    setHealth(health); setChecks(checks);
  }, [connectionId, path]);
  useEffect(() => { void load().catch(e => setError(e.message)); const timer = setInterval(() => { void load().catch(e => setError(e.message)); }, 5000); return () => clearInterval(timer); }, [load]);
  async function validate() {
    setBusy(true); setError('');
    try { await chatApi.validate(connectionId, destination.id); onChanged(); await load(); }
    catch (e) { setError((e as Error).message); onChanged(); } finally { setBusy(false); }
  }
  async function send() {
    if (!health) return;
    setBusy(true); setError('');
    try { await chatRequest(path, 'POST', { requestId: crypto.randomUUID(), connectionRevision: health.revision, destinationRevision: destination.revision }); await load(); }
    catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  const currentChecks = checks.filter(c => c.connectionRevision === health?.revision && c.destinationRevision === destination.revision);
  return <section className="chat-card" aria-label={`Verification for ${destination.displayName}`}><h3>{destination.displayName}</h3>
    <dl className="chat-verification">
      <dt>Credentials</dt><dd>{health?.credentials.map(c => `${c.id}: ${!c.configured ? 'missing' : c.externallyManaged ? 'externally managed' : 'protected'}${!c.available ? ' (store unavailable)' : ''}`).join(' · ') ?? 'Loading'}</dd>
      <dt>Destination access</dt><dd>{destination.accessState}</dd>
      <dt>Outbound delivery</dt><dd>{currentChecks.some(c => c.sentAt) ? 'Verified' : 'Not checked'}</dd>
      <dt>Inbound reply</dt><dd>{currentChecks.some(c => c.replyAt) ? 'Verified by check reply' : 'Not checked — reply “check” to a test message'}</dd>
      <dt>Live configuration</dt><dd>{health ? `Saved revision ${health.revision} · applied ${health.appliedRevision}` : 'Loading'}</dd>
    </dl>
    {health?.reason && <p className="chat-notice">{chatReason(health.reason)}</p>}
    <div className="chat-actions"><button type="button" disabled={busy} onClick={() => void validate()}>Validate channel access</button>
      <button type="button" disabled={busy || !health} onClick={() => void send()}>Send test message</button></div>
    {checks.map(c => <p className="chat-help" key={c.id}>{c.state} · {new Date(c.createdAt).toLocaleString()} · {c.replyAt ? 'Reply verified' : 'Reply not verified'}{c.reason ? ` · ${chatReason(c.reason)}` : ''}</p>)}
    {error && <p className="chat-error" role="alert">{error}</p>}
  </section>;
}