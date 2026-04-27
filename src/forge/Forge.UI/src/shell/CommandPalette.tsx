import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useHotkeys } from 'react-hotkeys-hook';
import { useBoardStore } from '@/state/useBoardStore';
import { usePrsStore } from '@/state/usePrsStore';
import { useWorkstreamsStore } from '@/state/useWorkstreamsStore';
import { useToastStore } from '@/state/useToastStore';
import { sampleState } from '@/sampleData';
import { useThemeStore } from '@/state/useThemeStore';
import { logAction } from '@/telemetry';

// Phase 7.1 — Cmd/Ctrl-K command palette. Sources:
//   * static views (jump-to)
//   * cards / PRs / workstreams (jump-to + light actions)
//   * theme + diagnostic actions
// Fuzzy matching is the simple "every search char in order" rule, weighted
// by prefix matches on the label so typing "p2" finds PR #229 quickly.

interface CommandItem {
  id: string;
  group: 'view' | 'card' | 'pr' | 'workstream' | 'action';
  label: string;
  hint?: string;
  run: () => void | Promise<void>;
}

function fuzzyScore(query: string, text: string): number | null {
  if (!query) return 0;
  const q = query.toLowerCase();
  const t = text.toLowerCase();
  if (t.startsWith(q)) return 1000 - t.length;
  if (t.includes(q)) return 500 - t.indexOf(q);
  // Subsequence match.
  let qi = 0;
  for (let i = 0; i < t.length && qi < q.length; i++) {
    if (t[i] === q[qi]) qi++;
  }
  return qi === q.length ? 100 - t.length : null;
}

export function CommandPalette() {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [highlight, setHighlight] = useState(0);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const navigate = useNavigate();

  const cards = useBoardStore((s) => s.cards);
  const prs = usePrsStore((s) => s.prs);
  const workstreams = useWorkstreamsStore((s) => s.workstreams);

  // Cmd/Ctrl-K toggles open. We listen on `mod+k` (react-hotkeys-hook).
  useHotkeys('mod+k', (e) => { e.preventDefault(); setOpen(true); }, { enableOnFormTags: true });
  useHotkeys('escape', () => setOpen(false), { enabled: open, enableOnFormTags: true });

  useEffect(() => {
    if (open) {
      setQuery('');
      setHighlight(0);
      // Defer focus to the next frame so the modal mounts first.
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  const items = useMemo<CommandItem[]>(() => {
    const wsList = workstreams.length > 0 ? workstreams : sampleState.workstreams;
    const result: CommandItem[] = [
      { id: 'view:overview',    group: 'view', label: 'Go to Overview',     hint: '/',            run: () => navigate('/') },
      { id: 'view:plan',        group: 'view', label: 'Go to Plan & Design', hint: '/plan',       run: () => navigate('/plan') },
      { id: 'view:timeline',    group: 'view', label: 'Go to Timeline',     hint: '/timeline',    run: () => navigate('/timeline') },
      { id: 'view:workstreams', group: 'view', label: 'Go to Workstreams',  hint: '/workstreams', run: () => navigate('/workstreams') },
      { id: 'view:prs',         group: 'view', label: 'Go to Pull Requests', hint: '/prs',        run: () => navigate('/prs') },
      { id: 'view:audit',       group: 'view', label: 'Go to Audit log',    hint: '/audit',       run: () => navigate('/audit') },
      { id: 'view:settings',    group: 'view', label: 'Go to Settings',     hint: '/settings',    run: () => navigate('/settings') },
      { id: 'action:theme', group: 'action', label: 'Toggle theme', hint: 'light ↔ dark', run: () => useThemeStore.getState().toggle() },
      { id: 'action:reload', group: 'action', label: 'Reload window', hint: 'hard refresh', run: () => window.location.reload() },
      { id: 'action:report', group: 'action', label: 'Report a problem', hint: '?', run: () => {
        document.dispatchEvent(new CustomEvent('forge:open-report'));
      } },
    ];

    for (const c of cards) {
      result.push({
        id: `card:${c.id}`, group: 'card',
        label: `${c.id} — ${c.title}`, hint: `${c.epic} · ${c.col}`,
        run: () => { useBoardStore.getState().setSelected(c.id); navigate('/plan'); },
      });
    }
    for (const p of prs) {
      result.push({
        id: `pr:${p.id}`, group: 'pr',
        label: `${p.id} — ${p.title}`, hint: `${p.branch} → ${p.base}`,
        run: () => { usePrsStore.getState().setSelected(p.id); navigate('/prs'); },
      });
    }
    for (const w of wsList) {
      result.push({
        id: `ws:${w.id}`, group: 'workstream',
        label: `${w.name}`, hint: `${w.branch} → ${w.base}`,
        run: () => { useWorkstreamsStore.getState().setFocused(w.id); navigate('/workstreams'); },
      });
      result.push({
        id: `ws:${w.id}:pr`, group: 'workstream',
        label: `Open PR for ${w.name}`, hint: `posts to provider`,
        run: () => {
          useWorkstreamsStore.getState().setFocused(w.id);
          navigate('/workstreams');
          useToastStore.getState().show(`Use the "PR" button on ${w.name}`, 'info');
        },
      });
    }
    return result;
  }, [cards, prs, workstreams, navigate]);

  const filtered = useMemo(() => {
    if (!query.trim()) return items.slice(0, 30);
    const scored = items
      .map((it) => ({ it, s: fuzzyScore(query.trim(), `${it.label} ${it.hint ?? ''}`) }))
      .filter((x): x is { it: CommandItem; s: number } => x.s !== null)
      .sort((a, b) => b.s - a.s)
      .slice(0, 30)
      .map((x) => x.it);
    return scored;
  }, [query, items]);

  useEffect(() => { setHighlight(0); }, [query]);

  function pick(i: number) {
    const it = filtered[i];
    if (!it) return;
    setOpen(false);
    logAction('palette.run', { id: it.id, group: it.group });
    void it.run();
  }

  if (!open) return null;
  return (
    <>
      <div className="modal-backdrop show" onClick={() => setOpen(false)} />
      <div className="modal show command-palette" role="dialog" aria-modal="true">
        <input
          ref={inputRef}
          className="cmd-input"
          placeholder="Jump to view, card, PR, workstream… (Esc to close)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight((h) => Math.min(h + 1, filtered.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight((h) => Math.max(h - 1, 0)); }
            else if (e.key === 'Enter')  { e.preventDefault(); pick(highlight); }
          }}
        />
        <div className="cmd-list">
          {filtered.length === 0 ? (
            <div className="cmd-empty">No matches for "{query}"</div>
          ) : filtered.map((it, i) => (
            <div
              key={it.id}
              className={`cmd-item${i === highlight ? ' active' : ''}`}
              onMouseEnter={() => setHighlight(i)}
              onMouseDown={(e) => { e.preventDefault(); pick(i); }}
              data-group={it.group}
            >
              <span className={`cmd-group ${it.group}`}>{it.group}</span>
              <span className="cmd-label">{it.label}</span>
              {it.hint && <span className="cmd-hint">{it.hint}</span>}
            </div>
          ))}
        </div>
      </div>
    </>
  );
}
