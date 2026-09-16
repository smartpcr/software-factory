import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { completeBrowserHandoff, getOperatorStatus, restoreLocalOperator, signInLocalOperator, requestPairingCode, localSignInUsable, operatorSessionChanged, pairOperator, unpairOperator, type OperatorStatus } from '@/api/operatorSession';

export function OperatorGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<OperatorStatus | null>(null);
  const [challenge, setChallenge] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [sentCode, setSentCode] = useState(false);
  const demo = new URLSearchParams(window.location.search).get('seed') === 'local';

  useEffect(() => {
    if (demo) return;
    let active = true;
    const refresh = () => {
      void (async () => {
        try { await completeBrowserHandoff(); }
        catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Sign-in failed.'); }
        try {
          const value = await getOperatorStatus();
          if (active) setStatus(value);
          try { const restored = await restoreLocalOperator(value); if (active) setStatus(restored); }
          catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Sign-in failed.'); }
        }
        catch (reason) { if (active) setError(reason instanceof Error ? reason.message : 'Connection failed.'); }
      })();
    };
    refresh();
    window.addEventListener(operatorSessionChanged, refresh);
    return () => { active = false; window.removeEventListener(operatorSessionChanged, refresh); };
  }, [demo]);

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
        <button type="button" onClick={() => {
          setError('');
          void getOperatorStatus().then(setStatus).catch((reason) => setError(reason instanceof Error ? reason.message : 'Connection failed.'));
        }} style={{ marginTop: 16 }}>Check again</button>
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
  </section>;
}
