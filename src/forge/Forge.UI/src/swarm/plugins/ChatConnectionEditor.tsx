import { useEffect, useState } from 'react';
import { chatApi, chatRequest, connectionPath, type ChatConnectionDetails, type ChatManagement } from './chatApi';

export function ChatConnectionEditor({ id, providerId, productId, management, onSaved, onCancel }: {
  id?: string; providerId: string; productId?: string; management: ChatManagement; onSaved: (id: string) => void; onCancel: () => void;
}) {
  const [connectionId] = useState(id ?? crypto.randomUUID());
  const [connection, setConnection] = useState<ChatConnectionDetails | null>(null);
  const [name, setName] = useState('');
  const [installation, setInstallation] = useState('');
  const [appId, setAppId] = useState('');
  const [privateChannels, setPrivateChannels] = useState(false);
  const [enabled, setEnabled] = useState(false);
  const [products, setProducts] = useState<string[]>(productId ? [productId] : []);
  const [values, setValues] = useState<Record<string, string>>({});
  const [references, setReferences] = useState<ChatConnectionDetails['secretReferences']>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    if (id) void chatApi.details(id).then(c => {
      if (!active) return;
      setConnection(c); setName(c.displayName); setInstallation(c.installationId); setAppId(String(c.settings.appId ?? ''));
      setPrivateChannels(c.settings.includePrivateChannels === true); setEnabled(c.enabled); setProducts(c.productIds); setReferences(c.secretReferences);
    }).catch(e => { if (active) setError(e.message); });
    return () => { active = false; };
  }, [id]);
  const credentialNames = providerId === 'slack' ? ['bot-token', 'app-token'] : ['bot-token'];
  async function save() {
    setBusy(true); setError('');
    try {
      const settings = providerId === 'slack' ? { appId, includePrivateChannels: privateChannels } : {};
      let c = await chatRequest<{ revision: number }>(connectionPath(connectionId), 'PUT', {
        expectedRevision: connection?.revision ?? 0, providerId, installationId: installation, displayName: name,
        enabled: false, settingsVersion: 1, settings, secretReferences: references, productIds: products,
      });
      for (const credential of credentialNames) {
        if (values[credential]) c = await chatRequest<{ revision: number }>(connectionPath(connectionId) + `/credentials/${credential}`, 'PUT', { expectedRevision: c.revision, value: values[credential] });
      }
      const saved = await chatApi.details(connectionId);
      if (enabled) await chatRequest(connectionPath(connectionId), 'PUT', { ...saved, expectedRevision: saved.revision, enabled: true });
      setValues({}); onSaved(connectionId);
    } catch (e) {
      setError((e as Error).message);
      // Keep the original revision on failure; adopting a concurrent writer's revision would silently overwrite their changes.
    } finally { setBusy(false); }
  }
  async function reload() {
    setBusy(true);
    try {
      const saved = await chatApi.details(connectionId); setConnection(saved); setReferences(saved.secretReferences); setValues({});
      setName(saved.displayName); setInstallation(saved.installationId); setAppId(String(saved.settings.appId ?? ''));
      setPrivateChannels(saved.settings.includePrivateChannels === true); setEnabled(saved.enabled); setProducts(saved.productIds); setError('');
    } catch (e) { setError((e as Error).message); } finally { setBusy(false); }
  }
  return <section className="chat-card" aria-label="Connection setup">
    <h3>{id ? 'Repair connection' : 'Set up connection'}</h3>
    <p className="chat-help">Save an incomplete connection with Enable connection unchecked, then return later to finish setup.</p>
    <fieldset disabled={busy || !!id && !connection}>
      <label>Connection name<input value={name} onChange={e => setName(e.target.value)} /></label>
      <label>{providerId === 'slack' ? 'Workspace ID' : 'Bot ID'}<input value={installation} disabled={!!connection} onChange={e => setInstallation(e.target.value)} /></label>
      {providerId === 'slack' && <><label>App ID<input value={appId} onChange={e => setAppId(e.target.value)} /></label>
        <label className="chat-checkline"><input type="checkbox" checked={privateChannels} onChange={e => setPrivateChannels(e.target.checked)} />Include private channels</label></>}
      {credentialNames.map(credential => <div className="chat-credential" key={credential}>
        <label>{credential === 'bot-token' ? 'Bot token' : 'App token'}<input type="password" autoComplete="new-password" value={values[credential] ?? ''}
          placeholder={references[credential] ? 'Saved · enter a replacement' : 'Not configured'} onChange={e => setValues(v => ({ ...v, [credential]: e.target.value }))} /></label>
        <details><summary>Externally managed reference</summary>
          <p className="chat-help">External credentials are read by the host. Changes to environment variables require a host refresh. Slack connections sharing an app must use the same app credential.</p>
          <label>Secret store<select value={references[credential]?.store ?? ''} onChange={e => setReferences(r => ({ ...r, [credential]: { store: e.target.value, key: r[credential]?.key ?? '' } }))}>
            <option value="">Select store</option>{management.secretStores.map(s => <option value={s.id} key={s.id}>{s.id}{s.externallyManaged ? ' · externally managed' : ''}</option>)}</select></label>
          <label>Reference key<input value={references[credential]?.key ?? ''} onChange={e => setReferences(r => ({ ...r, [credential]: { store: r[credential]?.store ?? 'environment', key: e.target.value } }))} /></label>
          {references[credential] && <button type="button" onClick={() => setReferences(r => { const copy = { ...r }; delete copy[credential]; return copy; })}>Remove reference</button>}
        </details>
      </div>)}
      <div><strong>Products allowed to use this connection</strong>{management.products.map(p => <label className="chat-checkline" key={p.id}>
        <input type="checkbox" checked={products.includes(p.id)} onChange={e => setProducts(ids => e.target.checked ? [...ids, p.id] : ids.filter(id => id !== p.id))} />{p.name}</label>)}</div>
      <label className="chat-checkline"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />Enable connection</label>
    </fieldset>
    {error && <><p className="chat-error" role="alert">{error}</p><button type="button" disabled={busy} onClick={() => void reload()}>Reload saved connection</button></>}
    <div className="chat-actions"><button type="button" disabled={busy} onClick={onCancel}>Cancel connection setup</button>
      <button type="button" disabled={busy || !name.trim() || !installation.trim() || providerId === 'slack' && !appId.trim() || !!id && !connection} onClick={() => void save()}>{busy ? 'Saving…' : 'Save connection'}</button></div>
  </section>;
}