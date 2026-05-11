// Renders the architect's decomposition (spawn.DecompositionJson) as either:
//  - cards grouped by phase (collapsible containers, dep chips per card,
//    yellow banner on floating / unresolved cards),
//  - raw JSON (the DB column shape, formatted),
//  - or YAML (the workstreams.yaml shape the architect emits / the operator
//    reads to validate the decomposition before approval).
//
// Phase 46 — replaces the flat list with phase containers grouped on
// each card's `phaseAnchor` field. Spawns produced before Phase 46
// (no phaseAnchor on any card) collapse to a single "Workstreams" group
// so the legacy view still works.
import { useEffect, useMemo, useState } from 'react';
import { forgeApi, type StorySpawnDecomposeWorkstream } from '@/api/forgeApi';

interface Props {
  storyId: string;
}

interface ParsedWorkstream extends StorySpawnDecomposeWorkstream {
  /** Index in the original decomposition order — used as a stable id. */
  ix: number;
}

type ViewMode = 'cards' | 'json' | 'yaml';

const DEFAULT_PHASE_ANCHOR = '__workstreams__';

interface PhaseGroup {
  anchor: string;
  /** Display label — derived from the anchor's `phase-{slug}` shape, or "Workstreams" for the ungrouped fallback. */
  label: string;
  items: ParsedWorkstream[];
}

function loadCollapsedFromStorage(storyId: string): Set<string> {
  if (typeof window === 'undefined') return new Set();
  try {
    const raw = window.localStorage.getItem(`forge.dag.collapsed.${storyId}`);
    if (!raw) return new Set();
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? new Set(parsed.filter((x): x is string => typeof x === 'string')) : new Set();
  } catch {
    return new Set();
  }
}

function persistCollapsedToStorage(storyId: string, collapsed: Set<string>) {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(
      `forge.dag.collapsed.${storyId}`,
      JSON.stringify([...collapsed]),
    );
  } catch {
    // Quota or insecure context — silent fall-through. The view still works,
    // just doesn't remember collapse state across sessions.
  }
}

function phaseLabelFromAnchor(anchor: string): string {
  if (anchor === DEFAULT_PHASE_ANCHOR) return 'Workstreams';
  // Strip the `phase-` prefix and convert kebab-case to Title Case.
  const stem = anchor.startsWith('phase-') ? anchor.slice(6) : anchor;
  return stem
    .split('-')
    .filter((w) => w.length > 0)
    .map((w) => w[0].toUpperCase() + w.slice(1))
    .join(' ');
}

function groupByPhase(items: ParsedWorkstream[]): PhaseGroup[] {
  const order: string[] = [];
  const buckets = new Map<string, ParsedWorkstream[]>();
  for (const w of items) {
    const anchor = w.phaseAnchor && w.phaseAnchor.length > 0 ? w.phaseAnchor : DEFAULT_PHASE_ANCHOR;
    if (!buckets.has(anchor)) {
      order.push(anchor);
      buckets.set(anchor, []);
    }
    buckets.get(anchor)!.push(w);
  }
  return order.map((anchor) => ({
    anchor,
    label: phaseLabelFromAnchor(anchor),
    items: buckets.get(anchor)!,
  }));
}

/**
 * A floating card has zero inbound edges (no upstream cites it as a dep
 * and it cites no upstream itself) AND no explicit start marker. The
 * only way the back-end signals "start" is via empty `deps` after a
 * resolved phase has no phase-deps either. We approximate at the UI
 * by treating "no deps + nobody downstream" as floating; the architect's
 * next iteration should add either a dep or the start marker. We also
 * bubble up `unresolvedDeps` as a separate yellow banner.
 */
function detectFloating(ws: ParsedWorkstream, all: ParsedWorkstream[]): boolean {
  const hasUpstream = (ws.deps ?? []).length > 0;
  const hasDownstream = all.some((w) => (w.deps ?? []).includes(ws.title));
  return !hasUpstream && !hasDownstream;
}

/**
 * Tiny YAML emitter scoped to the decomposition shape (sequence of
 * mappings with scalar / sequence-of-strings values). Avoids pulling
 * `js-yaml` (~70 KB) into the bundle just for a read-only view.
 */
function toYaml(items: StorySpawnDecomposeWorkstream[]): string {
  const lines: string[] = [];
  for (const w of items) {
    lines.push(`- title: ${yamlScalar(w.title)}`);
    if (w.description !== undefined) lines.push(`  description: ${yamlScalar(w.description)}`);
    if (w.acceptanceCriteria && w.acceptanceCriteria.length > 0) {
      lines.push(`  acceptanceCriteria:`);
      for (const ac of w.acceptanceCriteria) lines.push(`    - ${yamlScalar(ac)}`);
    }
    if (w.deps && w.deps.length > 0) {
      lines.push(`  deps:`);
      for (const d of w.deps) lines.push(`    - ${yamlScalar(d)}`);
    }
    if (w.deliverables && w.deliverables.length > 0) {
      lines.push(`  deliverables:`);
      for (const d of w.deliverables) lines.push(`    - ${yamlScalar(d)}`);
    }
    if (w.timeline) lines.push(`  timeline: ${yamlScalar(w.timeline)}`);
    if (w.estimatedEffort) lines.push(`  estimatedEffort: ${yamlScalar(w.estimatedEffort)}`);
    if (w.phaseAnchor) lines.push(`  phaseAnchor: ${yamlScalar(w.phaseAnchor)}`);
    if (w.unresolvedDeps && w.unresolvedDeps.length > 0) {
      lines.push(`  unresolvedDeps:`);
      for (const d of w.unresolvedDeps) lines.push(`    - ${yamlScalar(d)}`);
    }
  }
  return lines.join('\n') + '\n';
}

/**
 * YAML scalar quoting — quote strings that contain `: # & * ! | > ' " % @ \``
 * or that look like booleans / null / numbers, otherwise emit unquoted.
 * Multi-line strings aren't expected in the decomposition shape; if one
 * appears we still quote with double-quotes and escape the backslash and
 * embedded `"`.
 */
function yamlScalar(s: string | undefined): string {
  if (s === undefined || s === null) return 'null';
  const text = String(s);
  if (text.length === 0) return '""';
  // eslint-disable-next-line no-control-regex
  const reservedRe = /[:#&*!|>'"%@`]|\\u00|^[0-9-]|^(true|false|null|yes|no|on|off)$/i;
  if (text.includes('\n') || reservedRe.test(text) || /^\s|\s$/.test(text)) {
    return `"${text.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
  }
  return text;
}

export function DecompositionView({ storyId }: Props) {
  const [items, setItems] = useState<ParsedWorkstream[] | null>(null);
  const [rawJson, setRawJson] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [approved, setApproved] = useState(false);
  const [view, setView] = useState<ViewMode>('cards');
  const [copied, setCopied] = useState(false);
  const [collapsed, setCollapsed] = useState<Set<string>>(() => loadCollapsedFromStorage(storyId));

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.getStorySpawn(storyId);
        if (cancelled) return;
        setApproved(r.spawn.decompositionApproved);
        const raw = r.spawn.decompositionJson;
        if (!raw) {
          setItems([]);
          setRawJson('');
          return;
        }
        setRawJson(raw);
        const parsed = JSON.parse(raw) as StorySpawnDecomposeWorkstream[];
        if (!Array.isArray(parsed)) {
          setItems([]);
          return;
        }
        setItems(parsed.map((p, ix) => ({ ...p, ix })));
        setError(null);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load decomposition');
      }
    }
    void load();
    const id = setInterval(() => { void load(); }, 12_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId]);

  // Re-load collapsed set when the storyId changes — operators navigating
  // between spawns should see the per-spawn collapse state.
  useEffect(() => {
    setCollapsed(loadCollapsedFromStorage(storyId));
  }, [storyId]);

  const groups = useMemo(() => (items ? groupByPhase(items) : []), [items]);

  const prettyJson = useMemo(() => {
    if (!rawJson) return '';
    try { return JSON.stringify(JSON.parse(rawJson), null, 2); }
    catch { return rawJson; }
  }, [rawJson]);

  const yamlText = useMemo(() => (items && items.length > 0 ? toYaml(items) : ''), [items]);

  function togglePhase(anchor: string) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(anchor)) next.delete(anchor);
      else next.add(anchor);
      persistCollapsedToStorage(storyId, next);
      return next;
    });
  }

  async function copyView() {
    try {
      const payload = view === 'yaml' ? yamlText : prettyJson;
      await navigator.clipboard.writeText(payload);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard API can be denied (insecure context, headless test). Fail silent;
      // the user can still select-all + copy from the <pre> block.
    }
  }

  if (error) return <div className="doc-pane"><div className="doc-body"><pre style={{ color: 'var(--sc-red)' }}>{error}</pre></div></div>;
  if (items === null) return <div className="doc-pane"><div className="doc-body"><em style={{ color: 'var(--sc-text-dim)' }}>loading decomposition…</em></div></div>;
  if (items.length === 0) {
    return (
      <div className="doc-pane">
        <div className="doc-body">
          <div className="tab-placeholder">
            <div className="lbl">▌ DECOMPOSITION NOT YET PRODUCED</div>
            <div className="sub">
              The architect crew hasn't decomposed this story yet. Once
              <code> spawn.decompositionJson </code> is set (during the
              <code> decompose </code> phase), each workstream renders here
              as a card with deliverables + dep links.
            </div>
          </div>
        </div>
      </div>
    );
  }

  const totalCards = items.length;
  const phaseCount = groups.length;

  return (
    <div className="doc-pane decomp-view">
      <div className="doc-body" style={{ paddingLeft: 0 }}>
        <header className="decomp-header">
          <div className="decomp-header__title">
            ▌ DECOMPOSITION · {totalCards} workstream{totalCards === 1 ? '' : 's'} · {phaseCount} phase{phaseCount === 1 ? '' : 's'}
          </div>
          <div className="decomp-header__sub">
            {approved
              ? 'Approved · engineer crew is dispatching against these workstreams.'
              : 'Pending approval — orchestrator advances to IMPLEMENT once approved.'}
          </div>
          <div className="decomp-header__actions">
            <div className="decomp-view-toggle" role="tablist" aria-label="Decomposition view">
              <button
                type="button"
                role="tab"
                aria-selected={view === 'cards'}
                className={`decomp-view-toggle__btn${view === 'cards' ? ' is-active' : ''}`}
                onClick={() => setView('cards')}
              >diagram</button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'json'}
                className={`decomp-view-toggle__btn${view === 'json' ? ' is-active' : ''}`}
                onClick={() => setView('json')}
              >workstreams.json</button>
              <button
                type="button"
                role="tab"
                aria-selected={view === 'yaml'}
                className={`decomp-view-toggle__btn${view === 'yaml' ? ' is-active' : ''}`}
                onClick={() => setView('yaml')}
              >workstreams.yaml</button>
            </div>
            {(view === 'json' || view === 'yaml') && (
              <button
                type="button"
                className="decomp-view-toggle__copy"
                onClick={() => { void copyView(); }}
              >{copied ? 'copied ✓' : 'copy'}</button>
            )}
          </div>
        </header>
        {view === 'cards' ? (
          <div className="decomp-phases">
            {groups.map((g) => (
              <PhaseContainer
                key={g.anchor}
                group={g}
                allItems={items}
                collapsed={collapsed.has(g.anchor)}
                onToggle={() => togglePhase(g.anchor)}
              />
            ))}
          </div>
        ) : view === 'json' ? (
          <pre className="decomp-json"><code>{prettyJson}</code></pre>
        ) : (
          <pre className="decomp-json decomp-yaml"><code>{yamlText}</code></pre>
        )}
      </div>
    </div>
  );
}

function PhaseContainer({
  group,
  allItems,
  collapsed,
  onToggle,
}: {
  group: PhaseGroup;
  allItems: ParsedWorkstream[];
  collapsed: boolean;
  onToggle: () => void;
}) {
  return (
    <section className={`decomp-phase${collapsed ? ' is-collapsed' : ''}`}>
      <header className="decomp-phase__hd">
        <button
          type="button"
          className="decomp-phase__chevron"
          aria-expanded={!collapsed}
          aria-controls={`phase-body-${group.anchor}`}
          onClick={onToggle}
          title={collapsed ? 'expand phase' : 'collapse phase'}
        >{collapsed ? '▸' : '▾'}</button>
        <span className="decomp-phase__title">{group.label}</span>
        <span className="decomp-phase__count">{group.items.length} stage{group.items.length === 1 ? '' : 's'}</span>
      </header>
      {!collapsed && (
        <ul
          id={`phase-body-${group.anchor}`}
          className="decomp-list"
        >
          {group.items.map((w) => (
            <DecompositionCard key={w.ix} ws={w} all={allItems} />
          ))}
        </ul>
      )}
    </section>
  );
}

function DecompositionCard({ ws, all }: { ws: ParsedWorkstream; all: ParsedWorkstream[] }) {
  const tone = useMemo(() => effortTone(ws.estimatedEffort), [ws.estimatedEffort]);
  const upstream = useMemo(() => {
    const set = new Set(ws.deps ?? []);
    if (set.size === 0) return [] as ParsedWorkstream[];
    return all.filter((w) => set.has(w.title));
  }, [ws.deps, all]);
  const downstream = useMemo(() => {
    return all.filter((w) => (w.deps ?? []).includes(ws.title));
  }, [ws.title, all]);
  const isFloating = useMemo(() => detectFloating(ws, all), [ws, all]);
  const hasUnresolved = (ws.unresolvedDeps?.length ?? 0) > 0;

  return (
    <li className={`decomp-card${isFloating ? ' decomp-card--floating' : ''}`}>
      <header className="decomp-card__hd">
        <span className="decomp-card__no">WS · {String(ws.ix + 1).padStart(2, '0')}</span>
        <span className="decomp-card__title">{ws.title}</span>
        {ws.estimatedEffort && (
          <span className={`decomp-card__effort decomp-card__effort--${tone}`}>{ws.estimatedEffort}</span>
        )}
        {ws.timeline && <span className="decomp-card__timeline">{ws.timeline}</span>}
      </header>
      {(isFloating || hasUnresolved) && (
        <div className="decomp-card__warn">
          {isFloating && <span className="decomp-card__warn-banner">⚠ floating · no upstream or downstream</span>}
          {hasUnresolved && (
            <span className="decomp-card__warn-banner decomp-card__warn-banner--unresolved">
              ⚠ unresolved deps: {(ws.unresolvedDeps ?? []).join(', ')}
            </span>
          )}
        </div>
      )}
      {ws.description && (
        <p className="decomp-card__desc">{ws.description}</p>
      )}
      {(upstream.length > 0 || downstream.length > 0) && (
        <div className="decomp-card__deps">
          {upstream.length > 0 && (
            <div className="decomp-card__dep-line">
              <span className="decomp-card__dep-label">↥ depends on</span>
              {upstream.map((u) => (
                <span key={u.ix} className="decomp-card__dep-chip" title={u.description}>
                  WS·{String(u.ix + 1).padStart(2, '0')} {u.title}
                </span>
              ))}
            </div>
          )}
          {downstream.length > 0 && (
            <div className="decomp-card__dep-line">
              <span className="decomp-card__dep-label">↧ unblocks</span>
              {downstream.map((d) => (
                <span key={d.ix} className="decomp-card__dep-chip decomp-card__dep-chip--down" title={d.description}>
                  WS·{String(d.ix + 1).padStart(2, '0')} {d.title}
                </span>
              ))}
            </div>
          )}
        </div>
      )}
      {ws.acceptanceCriteria && ws.acceptanceCriteria.length > 0 && (
        <details className="decomp-card__details">
          <summary>{ws.acceptanceCriteria.length} acceptance criteri{ws.acceptanceCriteria.length === 1 ? 'on' : 'a'}</summary>
          <ul className="decomp-card__ac">
            {ws.acceptanceCriteria.map((ac, i) => (
              <li key={i}>{ac}</li>
            ))}
          </ul>
        </details>
      )}
      {ws.deliverables && ws.deliverables.length > 0 && (
        <details className="decomp-card__details">
          <summary>{ws.deliverables.length} deliverable{ws.deliverables.length === 1 ? '' : 's'}</summary>
          <ul className="decomp-card__deliv">
            {ws.deliverables.map((d, i) => (
              <li key={i}><code>{d}</code></li>
            ))}
          </ul>
        </details>
      )}
    </li>
  );
}

function effortTone(effort?: string | null): 'xs' | 'small' | 'medium' | 'large' | 'unknown' {
  if (!effort) return 'unknown';
  const e = effort.toLowerCase();
  if (e === 'xs' || e.includes('trivial')) return 'xs';
  if (e === 's' || e.includes('small')) return 'small';
  if (e === 'm' || e.includes('medium')) return 'medium';
  if (e === 'l' || e === 'xl' || e.includes('large')) return 'large';
  return 'unknown';
}

// Phase 46 — exported for vitest spec coverage of pure helpers.
export const __test__ = {
  DEFAULT_PHASE_ANCHOR,
  groupByPhase,
  detectFloating,
  toYaml,
  phaseLabelFromAnchor,
};
