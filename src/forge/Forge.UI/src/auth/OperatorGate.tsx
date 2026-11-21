import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { completeBrowserHandoff, getOperatorStatus, restoreLocalOperator, signInLocalOperator, requestPairingCode, issuePairingCode, isHostBrowser, localSignInUsable, operatorSessionChanged, pairOperator, unpairOperator, type IssuedPairingCode, type OperatorStatus } from '@/api/operatorSession';

export function OperatorGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<OperatorStatus | null>(null);
  const [challenge, setChallenge] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [sentCode, setSentCode] = useState(false);
  const [checkRequest, setCheckRequest] = useState(0);
  const demo = new URLSearchParams(window.location.search).get('seed') === 'local';

  useEffect(() => {
    if (demo) return;
    let active = true;
    let retry: number | undefined;
    const refresh = () => {
      window.clearTimeout(retry);
      void (async () => {
        try { await completeBrowserHandoff(); }
        catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Sign-in failed.'); }
        let value: OperatorStatus;
        try { value = await getOperatorStatus(); }
        catch (reason) {
          // Forge.Api restarting or not yet up: keep trying instead of stranding the page.
          if (active) { setError(reason instanceof Error ? reason.message : 'Connection failed.'); retry = window.setTimeout(refresh, 2000); }
          return;
        }
        if (!active) return;
        setError('');
        setStatus(value);
        try { const restored = await restoreLocalOperator(value); if (active) setStatus(restored); }
        catch (reason) {
          if (!active) return;
          setError(reason instanceof Error ? reason.message : 'Sign-in failed.');
          // The API went away between the status read and sign-in: retry the whole check.
          if (reason instanceof TypeError) retry = window.setTimeout(refresh, 2000);
        }
      })();
    };
    refresh();
    window.addEventListener(operatorSessionChanged, refresh);
    return () => { active = false; window.clearTimeout(retry); window.removeEventListener(operatorSessionChanged, refresh); };
  }, [demo, checkRequest]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError('');
    const code = challenge.trim();
    setChallenge('');
    try { await pairOperator(code); setStatus(await getOperatorStatus()); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Pairing failed.'); }
    finally { setBusy(false); }
  }

  async function localSignIn() {
    setBusy(true); setError('');
    try { await signInLocalOperator(); setStatus(await getOperatorStatus()); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Sign-in failed.'); }
    finally { setBusy(false); }
  }

  async function sendCode() {
    setBusy(true); setError(''); setSentCode(false);
    try { await requestPairingCode(); setSentCode(true); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not send a code.'); }
    finally { setBusy(false); }
  }

  const localReady = localSignInUsable(status);

  if (demo || status?.state === 'Paired') return <>{children}</>;
  return (
    <main style={{ minHeight: '100vh', display: 'grid', placeItems: 'center', background: 'var(--bg, #0b0d10)', color: 'var(--text, #e6e7e8)' }}>
      <section aria-labelledby="operator-title" style={{ width: 'min(480px, 90vw)', padding: 32, border: '1px solid #34383f', borderTop: '3px solid #f6ab40', borderRadius: 8 }}>
        <div style={{ letterSpacing: '.2em', color: '#f6ab40', marginBottom: 24 }}>FORGE</div>
        <h1 id="operator-title">{localReady ? 'Sign in to Forge' : status?.state ?? 'Connecting to Forge'}</h1>
        {status?.state === 'Needs operator enrollment' && !localReady ? (
          <p>A host administrator must enroll your operator identity on this computer. Then run the Pair browser command and return here.</p>
        ) : status?.state === 'Pair browser' || localReady ? (
          <form onSubmit={(event) => { void submit(event); }}>
            {localReady && <>
              <p>Forge signs you in automatically from the Windows account running this host.</p>
              <button type="button" disabled={busy} onClick={() => { void localSignIn(); }} style={{ padding: '10px 20px', marginBottom: 16 }}>
                {busy ? 'Signing in…' : 'Sign in with this Windows account'}
              </button>
            </>}
            <details open={!localReady}>
            <summary>Use a pairing code</summary>
            <p>A pairing code lets you choose another enrolled account or sign in when automatic sign-in is unavailable.</p>
            <label htmlFor="pairing-code">Pairing code</label>
            <input id="pairing-code" type="password" autoComplete="off" spellCheck={false} maxLength={64}
              value={challenge} onChange={(event) => setChallenge(event.target.value)} required
              style={{ display: 'block', width: '100%', boxSizing: 'border-box', padding: 12, margin: '8px 0 16px', background: '#151a21', color: 'inherit', border: '1px solid #50555d' }} />
            <button type="submit" disabled={busy || challenge.trim().length !== 64} style={{ padding: '10px 20px' }}>{busy ? 'Pairing…' : 'Pair browser'}</button>
            {status?.codeDeliveryAvailable && <p>
              <button type="button" disabled={busy} onClick={() => { void sendCode(); }} style={{ padding: '10px 20px' }}>
                {busy ? 'Sending…' : 'Send me a pairing code'}
              </button>
              {sentCode && <span role="status" style={{ marginLeft: 12, color: '#a7adb7' }}>Sent to your chat. Paste it above within two minutes.</span>}
            </p>}
            </details>
            <p style={{ color: '#a7adb7', fontSize: 13 }}>Sessions last up to eight hours. Signing out keeps this browser signed out until you sign in again.</p>
          </form>
        ) : null}
        <button type="button" onClick={() => { setError(''); setCheckRequest((n) => n + 1); }} style={{ marginTop: 16 }}>Check again</button>
        {error && <p role="alert" style={{ color: '#ffb0a7' }}>{error}</p>}
      </section>
    </main>
  );
}

export function OperatorSessionSettings() {
  const [error, setError] = useState('');
  return <section style={{ padding: 16, border: '1px solid #34383f', marginBottom: 16 }}>
    <h3>Browser session</h3>
    <p>This browser is paired with your enrolled Forge operator.</p>
    <button onClick={() => { void unpairOperator().catch(() => setError('Could not end this session.')); }}>Unpair this browser</button>
    {error && <p role="alert">{error}</p>}
    {isHostBrowser() && <PairAnotherBrowser />}
  </section>;
}

/** Shows a one-use code for signing in a browser on another machine as this same operator. */
function PairAnotherBrowser() {
  const [code, setCode] = useState<IssuedPairingCode | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState('');
  const secondsLeft = code ? Math.max(0, Math.ceil((code.expiresAt - now) / 1000)) : 0;

  useEffect(() => {
    if (!code) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [code]);

  // Drop the secret from the page as soon as it can no longer be used.
  useEffect(() => { if (code && secondsLeft === 0) { setCode(null); setCopied(false); } }, [code, secondsLeft]);

  async function issue() {
    setBusy(true); setError(''); setCopied(false);
    try { setCode(await issuePairingCode()); setNow(Date.now()); }
    catch (reason) { setCode(null); setError(reason instanceof Error ? reason.message : 'Could not issue a pairing code.'); }
    finally { setBusy(false); }
  }

  async function copy() {
    if (!code) return;
    try { await navigator.clipboard.writeText(code.challenge); setCopied(true); }
    catch { setError('Copy failed. Select the code and copy it manually.'); }
  }

  return <div style={{ marginTop: 16, paddingTop: 16, borderTop: '1px solid #34383f' }}>
    <h4 style={{ margin: '0 0 6px' }}>Pair another browser</h4>
    <p style={{ margin: '0 0 10px', color: '#a7adb7', fontSize: 13 }}>
      Get a one-use code that signs a browser on another machine into Forge as you. Paste it into that
      browser's Pair browser form within two minutes. A new code replaces the previous one.
    </p>
    <button type="button" disabled={busy} onClick={() => { void issue(); }}>
      {busy ? 'Issuing…' : code ? 'Issue a new code' : 'Issue pairing code'}
    </button>
    {code && <div style={{ marginTop: 10 }}>
      <code aria-label="Pairing code" style={{ display: 'block', padding: 10, background: '#151a21', border: '1px solid #50555d', wordBreak: 'break-all', userSelect: 'all' }}>
        {code.challenge}
      </code>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 8 }}>
        <button type="button" onClick={() => { void copy(); }}>{copied ? 'Copied' : 'Copy'}</button>
        <span role="status" style={{ color: '#a7adb7', fontSize: 13 }}>Expires in {secondsLeft}s</span>
      </div>
    </div>}
    {error && <p role="alert" style={{ color: '#ffb0a7' }}>{error}</p>}
  </div>;
}
