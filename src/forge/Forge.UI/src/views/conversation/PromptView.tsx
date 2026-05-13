// Insight I.4 (docs/forge/workstream-insights.md) — verbatim
// system + user prompt for the active iter, per role. Sourced from
// `task.prompt` events emitted by GeneratorDelegate / EvaluatorDelegate.
// Operator-facing audit: "what context did the agent actually see"
// for the iter whose chip is selected.

import { useMemo, useState } from 'react';
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';

interface PromptPayload {
  agentId?: string;
  crewId?: string;
  systemPrompt?: string | null;
  userPrompt?: string;
  model?: string;
  backend?: string;
  invokeMode?: string;
  role?: string;
  iter?: number;
}

interface Props {
  /** Full transcript; we filter to task.prompt locally so the view
   *  follows the live store without an extra fetch. */
  events: TaskTranscriptEvent[];
  /** 1-based iter number to display. When null the latest iter wins. */
  activeIter: number | null;
}

export function PromptView({ events, activeIter }: Props): JSX.Element {
  const prompts = useMemo(() => {
    const list: PromptPayload[] = [];
    for (const e of events) {
      if (e.kind !== 'task.prompt') continue;
      const p = (e.payload ?? {}) as PromptPayload;
      list.push(p);
    }
    return list;
  }, [events]);

  // Determine the active iter — explicit selection wins, else latest.
  const iter = useMemo(() => {
    if (activeIter !== null && activeIter !== undefined) return activeIter;
    let latest = 0;
    for (const p of prompts) if ((p.iter ?? 0) > latest) latest = p.iter ?? 0;
    return latest > 0 ? latest : null;
  }, [activeIter, prompts]);

  const forIter = useMemo(
    () => prompts.filter((p) => p.iter === iter),
    [prompts, iter],
  );

  if (prompts.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO PROMPTS YET</div>
        <div className="sub">
          Once the gen↔eval loop fires its first <code>task.prompt</code>
          event, this tab will show the verbatim system + user prompt
          the agent saw — useful for auditing how the context grew
          across iterations.
        </div>
      </div>
    );
  }

  if (forIter.length === 0) {
    return (
      <div className="tab-placeholder">
        <em>No prompts recorded for iter {iter ?? '—'}.</em>
      </div>
    );
  }

  // Generator first, evaluator second.
  const ordered = [...forIter].sort((a, b) => roleOrder(a.role) - roleOrder(b.role));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: 8 }}>
      <header style={{ fontSize: 11, color: 'var(--text-secondary, #8d94a3)', letterSpacing: '0.06em' }}>
        Prompts for iter {iter}
      </header>
      {ordered.map((p, i) => (
        <PromptCard key={`${p.role ?? 'role'}-${i}`} prompt={p} />
      ))}
    </div>
  );
}

function PromptCard({ prompt }: { prompt: PromptPayload }) {
  const [sysOpen, setSysOpen] = useState(false);
  const role = prompt.role ?? 'unknown';
  const sysLen = prompt.systemPrompt?.length ?? 0;
  const userLen = prompt.userPrompt?.length ?? 0;
  return (
    <section
      style={{
        border: '1px solid var(--border, #2a2f3a)',
        borderRadius: 3,
        background: 'var(--bg-elevated, rgba(255,255,255,0.02))',
        fontFamily: 'var(--sc-font-mono, monospace)',
        fontSize: 12,
      }}
    >
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '6px 10px',
          borderBottom: '1px solid var(--border, #2a2f3a)',
          fontSize: 11,
          letterSpacing: '0.04em',
        }}
      >
        <span style={{ color: roleColor(role), fontWeight: 700, textTransform: 'uppercase' }}>
          {role}
        </span>
        {prompt.model && (
          <span style={{ color: 'var(--text-secondary, #8d94a3)' }}>{prompt.model}</span>
        )}
        {(prompt.backend || prompt.invokeMode) && (
          <span style={{ color: 'var(--text-tertiary, #8d94a3)' }}>
            {prompt.backend ?? ''}{prompt.backend && prompt.invokeMode ? ' / ' : ''}{prompt.invokeMode ?? ''}
          </span>
        )}
        {prompt.agentId && (
          <span style={{ marginLeft: 'auto', color: 'var(--text-tertiary, #8d94a3)' }}>
            agent {prompt.agentId}
          </span>
        )}
      </header>

      {prompt.systemPrompt && (
        <div style={{ padding: '6px 10px', borderBottom: '1px solid var(--border, #2a2f3a)' }}>
          <button
            type="button"
            onClick={() => setSysOpen((v) => !v)}
            style={{
              background: 'transparent',
              border: 0,
              color: 'var(--text-secondary, #8d94a3)',
              cursor: 'pointer',
              fontFamily: 'inherit',
              fontSize: 11,
              padding: 0,
              letterSpacing: '0.04em',
            }}
            aria-expanded={sysOpen}
          >
            {sysOpen ? '▾' : '▸'} system prompt ({formatBytes(sysLen)})
          </button>
          {sysOpen && (
            <pre
              style={{
                marginTop: 4,
                marginBottom: 0,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
                color: 'var(--text-primary, #e3e6eb)',
                fontFamily: 'inherit',
                fontSize: 11,
                lineHeight: 1.5,
                maxHeight: 320,
                overflow: 'auto',
              }}
            >
              {prompt.systemPrompt}
            </pre>
          )}
        </div>
      )}

      <div style={{ padding: '6px 10px' }}>
        <div style={{ fontSize: 11, color: 'var(--text-secondary, #8d94a3)', letterSpacing: '0.04em', marginBottom: 4 }}>
          user prompt ({formatBytes(userLen)})
        </div>
        <pre
          style={{
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            color: 'var(--text-primary, #e3e6eb)',
            fontFamily: 'inherit',
            fontSize: 11,
            lineHeight: 1.5,
            margin: 0,
            maxHeight: 600,
            overflow: 'auto',
          }}
        >
          {prompt.userPrompt ?? ''}
        </pre>
      </div>
    </section>
  );
}

function roleOrder(role: string | undefined): number {
  const lc = (role ?? '').toLowerCase();
  if (lc === 'generator') return 0;
  if (lc === 'evaluator') return 1;
  return 2;
}

function roleColor(role: string): string {
  const lc = role.toLowerCase();
  if (lc === 'generator') return 'var(--sc-cyan, #5eead4)';
  if (lc === 'evaluator') return 'var(--sc-amber, #ffb347)';
  return 'var(--text-primary, #e3e6eb)';
}

function formatBytes(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2)}MB`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}KB`;
  return `${n}B`;
}
