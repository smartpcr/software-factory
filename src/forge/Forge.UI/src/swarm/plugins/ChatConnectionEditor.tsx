import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { chatApi, chatRequest, connectionPath, type ChatConnectionDetails, type ChatManagement } from './chatApi';
import { ChatSetupGuide } from './ChatSetupGuide';
import { ChatConnectionAuthorize } from './ChatConnectionAuthorize';

export interface ChatConnectionEditorProps {
  id?: string; providerId: string; productId?: string; management: ChatManagement; onSaved: (id: string) => void; onCancel: () => void;
  actionsTarget?: HTMLElement | null; onBusyChange?: (busy: boolean) => void;
}
export function ChatConnectionEditor(props: ChatConnectionEditorProps) {
  const [manual, setManual] = useState(!!props.id || !['slack', 'telegram'].includes(props.providerId));
  return manual ? <ManualConnectionEditor {...props} /> : <ChatConnectionAuthorize {...props} onManual={() => setManual(true)} />;
}

function ManualConnectionEditor({ id, providerId, productId, management, onSaved, onCancel, actionsTarget, onBusyChange }: ChatConnectionEditorProps) {
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
  useEffect(() => { onBusyChange?.(busy); }, [busy, onBusyChange]);
  useEffect(() => () => onBusyChange?.(false), [onBusyChange]);
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
  const actions = <div className="chat-actions chat-editor-actions"><button type="button" disabled={busy} onClick={onCancel}>Cancel connection setup</button>
    <button type="button" className="primary" disabled={busy || !name.trim() || !installation.trim() || providerId === 'slack' && !appId.trim() || !!id && !connection} onClick={() => void save()}>{busy ? 'Saving…' : 'Save connection'}</button></div>;
  return <section className="chat-card chat-connection-editor" aria-label="Connection setup">
    <header className="chat-editor-heading"><div><span className="chat-eyebrow">{providerId === 'slack' ? 'SLACK' : 'TELEGRAM'} CONNECTION</span>
      <h3>{id ? 'Repair connection' : 'Set up connection'}</h3></div>
      {!actionsTarget && <button type="button" aria-label="Close connection setup" disabled={busy} onClick={onCancel}>Close</button>}</header>
    <ChatSetupGuide providerId={providerId} />
    <fieldset disabled={busy || !!id && !connection}>
      <details className="chat-section" open><summary>Connection details</summary><div className="chat-section-content">
        <label>Connection name<input autoFocus={!id} data-chat-initial-focus value={name} placeholder={providerId === 'slack' ? 'Forge engineering' : 'Forge operations bot'} onChange={e => setName(e.target.value)} /></label>
        <p className="chat-field-help">A name you will recognize when choosing a connection for a story.</p>
        <label>{providerId === 'slack' ? 'Workspace ID' : 'Bot ID'}<input value={installation} placeholder={providerId === 'slack' ? 'T012ABCDEF' : '123456789'} disabled={!!connection} onChange={e => setInstallation(e.target.value)} /></label>
        <p className="chat-field-help">{providerId === 'slack' ? 'Copy the T… segment from app.slack.com/client/T…/C… in your Slack browser address.' : 'Use the digits before the colon in the token BotFather gave you.'}</p>
        {providerId === 'slack' && <><label>App ID<input value={appId} placeholder="A012ABCDEF" onChange={e => setAppId(e.target.value)} /></label>
          <p className="chat-field-help">Slack app settings → Basic Information → App Credentials → App ID.</p>
          <label className="chat-checkline"><input type="checkbox" checked={privateChannels} onChange={e => setPrivateChannels(e.target.checked)} />Include private channels</label>
          <p className="chat-field-help">Private channels also need groups:read, groups:history and message.groups in the Slack app. Reinstall it after adding permissions and invite it to the channel.</p></>}
      </div></details>
      <details className="chat-section" open><summary>Credentials</summary><div className="chat-section-content">
        {credentialNames.map(credential => <div className="chat-credential" key={credential}>
          <label>{credential === 'bot-token' ? 'Bot token' : 'App token'}<input type="password" autoComplete="new-password" value={values[credential] ?? ''}
            placeholder={references[credential] ? 'Saved · enter a replacement' : 'Not configured'} onChange={e => setValues(v => ({ ...v, [credential]: e.target.value }))} /></label>
          <p className="chat-field-help">{providerId !== 'slack' ? 'Paste the complete BotFather token, including the numeric prefix and colon.' : credential === 'bot-token'
            ? 'Paste the xoxb-… Bot User OAuth Token from OAuth & Permissions after installing the app.'
            : 'Paste the xapp-… App-Level Token from Basic Information. It must include connections:write; Socket Mode must be enabled.'}</p>
          <details><summary>Externally managed reference</summary>
            <p className="chat-help">External credentials are read by the host. Changes to environment variables require a host refresh. Slack connections sharing an app must use the same app credential.</p>
            <label>Secret store<select value={references[credential]?.store ?? ''} onChange={e => setReferences(r => ({ ...r, [credential]: { store: e.target.value, key: r[credential]?.key ?? '' } }))}>
              <option value="">Select store</option>{management.secretStores.map(s => <option value={s.id} key={s.id}>{s.id}{s.externallyManaged ? ' · externally managed' : ''}</option>)}</select></label>
            <label>Reference key<input value={references[credential]?.key ?? ''} onChange={e => setReferences(r => ({ ...r, [credential]: { store: r[credential]?.store ?? 'environment', key: e.target.value } }))} /></label>
            {references[credential] && <button type="button" onClick={() => setReferences(r => { const copy = { ...r }; delete copy[credential]; return copy; })}>Remove reference</button>}
          </details>
        </div>)}
      </div></details>
      <details className="chat-section"><summary>Products allowed to use this connection · {products.length} selected</summary><div className="chat-section-content">
        <p className="chat-help">Select the products whose owners may choose this connection for their stories.</p>
        {management.products.map(p => <label className="chat-checkline" key={p.id}>
          <input type="checkbox" checked={products.includes(p.id)} onChange={e => setProducts(ids => e.target.checked ? [...ids, p.id] : ids.filter(id => id !== p.id))} />{p.name}</label>)}
      </div></details>
      <label className="chat-checkline"><input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} />Enable connection</label>
      <p className="chat-help">Keep this unchecked to save an incomplete connection and finish later. After saving, choose a channel for your story.</p>
    </fieldset>
    {error && <><p className="chat-error" role="alert">{error}</p><button type="button" disabled={busy} onClick={() => void reload()}>Reload saved connection</button></>}
    {actionsTarget ? createPortal(actions, actionsTarget) : actions}
  </section>;
}