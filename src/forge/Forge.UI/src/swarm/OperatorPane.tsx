import { useCallback, useEffect, useMemo, useState } from 'react';
import { forgeApi, type OperatorPrompt } from '@/api/forgeApi';
import { useTaskLive } from '@/realtime/useTaskLiveStore';

interface Props {
  /**
   * Work-item id whose operator-steering prompts to render. For workstreams
   * this is the workstream id (the crew runs the gen↔eval loop with it as
   * workItemId); for plan-doc work it is the synthetic plan-doc id.
   */
  workItemId: string | null;
}

/**
 * Operator tab content (Phase 92) — a human operator enters a free-text
 * steering / follow-up directive that is injected into BOTH the generator and
 * the evaluator on every subsequent iteration (persistent + cumulative) until
 * dismissed. Active prompts render with a Dismiss button; dismissed ones are
 * kept in a muted history so the operator can see what was previously in play.
 *
 * Data source: GET/POST /api/workstreams/{id}/operator-prompts. The list is
 * re-fetched on mount and whenever a `task.operator.steer*` transcript event
 * lands (so a steer posted from another browser session shows up live).
 */
export function OperatorPane({ workItemId }: Props) {
  const live = useTaskLive(workItemId);
  const transcript = live?.transcript ?? [];

  const [prompts, setPrompts] = useState<OperatorPrompt[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);

  // Coalesce a re-fetch trigger off the last operator-steer transcript event so
  // a steer/dismiss from any session refreshes this list without polling.
  const steerSignature = useMemo(() => {
    let count = 0;
    let last = '';
    for (const e of transcript) {
      if (e.kind === 'task.operator.steer' || e.kind === 'task.operator.steer-dismissed') {
        count++;
        last = String(e.id ?? e.kind);
      }
    }
    return `${count}:${last}`;
  }, [transcript]);

  const reload = useCallback(() => {
    if (!workItemId) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    forgeApi.fetchOperatorPrompts(workItemId)
      .then((r) => { if (!cancelled) setPrompts(r); })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [workItemId]);

  useEffect(() => {
    const cleanup = reload();
    return cleanup;
    // steerSignature intentionally in deps so a live steer refreshes the list.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workItemId, steerSignature]);

  const onSend = useCallback(async () => {
    if (!workItemId) return;
    const body = draft.trim();
    if (!body) return;
    setSending(true);
    setError(null);
    try {
      const created = await forgeApi.addOperatorPrompt(workItemId, body);
      setPrompts((prev) => [created, ...prev]);
      setDraft('');
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  }, [workItemId, draft]);

  const onDismiss = useCallback(async (promptId: string) => {
    if (!workItemId) return;
    setBusyId(promptId);
    setError(null);
    try {
      const updated = await forgeApi.dismissOperatorPrompt(workItemId, promptId);
      setPrompts((prev) => prev.map((p) => (p.id === promptId ? updated : p)));
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusyId(null);
    }
  }, [workItemId]);

  if (!workItemId) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ OPERATOR</div>
        <div className="sub">Select a workstream to steer its generator and evaluator.</div>
      </div>
    );
  }

  const active = prompts.filter((p) => !p.dismissedAt);
  const dismissed = prompts.filter((p) => p.dismissedAt);

  return (
    <div className="swarm-operator" style={{ padding: 8, overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ fontSize: 11, color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono, monospace)', lineHeight: 1.5 }}>
        Steer or follow up as the human operator. Each active directive below is
        injected into <strong>both</strong> the generator and the evaluator on every
        iteration until you dismiss it.
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') { e.preventDefault(); void onSend(); }
          }}
          placeholder="e.g. Prioritise correctness over speed; the public API must stay backward compatible…"
          rows={4}
          style={{
            width: '100%',
            resize: 'vertical',
            fontFamily: 'var(--sc-font-mono, monospace)',
            fontSize: 12,
            padding: '8px 10px',
            borderRadius: 4,
            border: '1px solid var(--sc-border)',
            background: 'var(--sc-surface)',
            color: 'var(--sc-text)',
            boxSizing: 'border-box',
          }}
        />
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <button
            type="button"
            onClick={() => void onSend()}
            disabled={sending || draft.trim().length === 0}
            style={{
              fontFamily: 'var(--sc-font-display, monospace)',
              fontSize: 10,
              letterSpacing: '0.14em',
              textTransform: 'uppercase',
              padding: '7px 16px',
              borderRadius: 4,
              border: '1px solid var(--sc-amber, #d99a2b)',
              background: 'var(--sc-amber, #d99a2b)',
              color: '#1a1a1a',
              cursor: sending || draft.trim().length === 0 ? 'default' : 'pointer',
              opacity: sending || draft.trim().length === 0 ? 0.5 : 1,
            }}
          >
            {sending ? 'Sending…' : 'Send directive'}
          </button>
          <span style={{ fontSize: 10, color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono, monospace)' }}>
            ⌘/Ctrl + Enter to send
          </span>
        </div>
      </div>

      {error && (
        <div style={{
          fontSize: 11,
          color: 'var(--sc-red, rgb(220, 38, 38))',
          background: 'rgba(255, 107, 128, 0.06)',
          border: '1px solid var(--sc-red-d, rgb(220, 38, 38))',
          padding: '6px 10px',
          borderRadius: 4,
          fontFamily: 'var(--sc-font-mono, monospace)',
        }}>
          ⚠ {error}
        </div>
      )}

      <Section label={`Active (${active.length})`}>
        {active.length === 0 ? (
          <Empty text={loading ? 'Loading…' : 'No active directives. The crew runs unsteered.'} />
        ) : (
          active.map((p) => (
            <PromptCard key={p.id} prompt={p} busy={busyId === p.id} onDismiss={() => void onDismiss(p.id)} />
          ))
        )}
      </Section>

      {dismissed.length > 0 && (
        <Section label={`Dismissed (${dismissed.length})`}>
          {dismissed.map((p) => (
            <PromptCard key={p.id} prompt={p} busy={false} onDismiss={null} />
          ))}
        </Section>
      )}
    </div>
  );
}

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{
        fontFamily: 'var(--sc-font-display, monospace)',
        fontSize: 10,
        letterSpacing: '0.18em',
        textTransform: 'uppercase',
        color: 'var(--sc-text-dim)',
      }}>
        {label}
      </div>
      {children}
    </div>
  );
}

function Empty({ text }: { text: string }) {
  return (
    <div style={{ fontSize: 11, color: 'var(--sc-text-dim)', fontStyle: 'italic', fontFamily: 'var(--sc-font-mono, monospace)' }}>
      {text}
    </div>
  );
}

function PromptCard({ prompt, busy, onDismiss }: { prompt: OperatorPrompt; busy: boolean; onDismiss: (() => void) | null }) {
  const isDismissed = !!prompt.dismissedAt;
  return (
    <div style={{
      border: '1px solid var(--sc-border)',
      borderLeft: isDismissed ? '3px solid var(--sc-border)' : '3px solid var(--sc-amber, #d99a2b)',
      borderRadius: 4,
      padding: '8px 10px',
      background: 'var(--sc-surface)',
      opacity: isDismissed ? 0.55 : 1,
    }}>
      <div style={{
        whiteSpace: 'pre-wrap',
        fontFamily: 'var(--sc-font-mono, monospace)',
        fontSize: 12,
        color: 'var(--sc-text)',
        lineHeight: 1.45,
      }}>
        {prompt.body}
      </div>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginTop: 6, gap: 8 }}>
        <span style={{ fontSize: 10, color: 'var(--sc-text-dim)', fontFamily: 'var(--sc-font-mono, monospace)' }}>
          {prompt.createdBy ? `${prompt.createdBy} · ` : ''}
          {prompt.createdAt ? new Date(prompt.createdAt).toLocaleString() : ''}
          {isDismissed && prompt.dismissedAt ? ` · dismissed ${new Date(prompt.dismissedAt).toLocaleString()}` : ''}
        </span>
        {onDismiss && (
          <button
            type="button"
            onClick={onDismiss}
            disabled={busy}
            style={{
              fontFamily: 'var(--sc-font-display, monospace)',
              fontSize: 9,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              padding: '4px 10px',
              borderRadius: 4,
              border: '1px solid var(--sc-border)',
              background: 'transparent',
              color: 'var(--sc-text-dim)',
              cursor: busy ? 'default' : 'pointer',
              opacity: busy ? 0.5 : 1,
            }}
          >
            {busy ? 'Dismissing…' : 'Dismiss'}
          </button>
        )}
      </div>
    </div>
  );
}
