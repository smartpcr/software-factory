// Renders the architect's decomposition (spawn.DecompositionJson) as a
// list of workstream cards OR as raw formatted JSON, toggle-switched so
// the operator can read what each engineer pair is going to build either
// as a diagram (cards + dep arrows) or as the source-of-truth JSON.
import { useEffect, useMemo, useState } from 'react';
import { forgeApi, type StorySpawnDecomposeWorkstream } from '@/api/forgeApi';

interface Props {
  storyId: string;
}

interface ParsedWorkstream extends StorySpawnDecomposeWorkstream {
  /** Index in the original decomposition order — used as a stable id. */
  ix: number;
}

type ViewMode = 'cards' | 'json';

export function DecompositionView({ storyId }: Props) {
  const [items, setItems] = useState<ParsedWorkstream[] | null>(null);
  const [rawJson, setRawJson] = useState<string>('');
  const [error, setError] = useState<string | null>(null);
  const [approved, setApproved] = useState(false);
  const [view, setView] = useState<ViewMode>('cards');
  const [copied, setCopied] = useState(false);

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

  // Pretty-printed JSON for the json view. Re-format the stored string so
  // the indentation is consistent regardless of how the server serialised it.
  const prettyJson = useMemo(() => {
    if (!rawJson) return '';
    try { return JSON.stringify(JSON.parse(rawJson), null, 2); }
    catch { return rawJson; }
  }, [rawJson]);

  async function copyJson() {
    try {
      await navigator.clipboard.writeText(prettyJson);
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

  return (
    <div className="doc-pane decomp-view">
      <div className="doc-body" style={{ paddingLeft: 0 }}>
        <header className="decomp-header">
          <div className="decomp-header__title">
            ▌ DECOMPOSITION · {items.length} parallel workstream{items.length === 1 ? '' : 's'}
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
            </div>
            {view === 'json' && (
              <button
                type="button"
                className="decomp-view-toggle__copy"
                onClick={() => { void copyJson(); }}
              >{copied ? 'copied ✓' : 'copy'}</button>
            )}
          </div>
        </header>
        {view === 'cards' ? (
          <ul className="decomp-list">
            {items.map((w) => (
              <DecompositionCard key={w.ix} ws={w} all={items} />
            ))}
          </ul>
        ) : (
          <pre className="decomp-json"><code>{prettyJson}</code></pre>
        )}
      </div>
    </div>
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

  return (
    <li className="decomp-card">
      <header className="decomp-card__hd">
        <span className="decomp-card__no">WS · {String(ws.ix + 1).padStart(2, '0')}</span>
        <span className="decomp-card__title">{ws.title}</span>
        {ws.estimatedEffort && (
          <span className={`decomp-card__effort decomp-card__effort--${tone}`}>{ws.estimatedEffort}</span>
        )}
        {ws.timeline && <span className="decomp-card__timeline">{ws.timeline}</span>}
      </header>
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
