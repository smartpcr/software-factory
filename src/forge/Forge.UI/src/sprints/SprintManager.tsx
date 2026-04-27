// Phase 15 — shared sprint manager modal. Embedded by both Plan & Design
// (page-head "Manage…" entry) and Products (per-card "Manage sprints"
// button). Single component; the caller passes the productId.
//
// Reads via `useSprintsStore` (per-product cache) and writes via the
// Phase 15 API endpoints — discovery, bulk import, default-area toggle,
// saved-prefix save / refresh / delete.

import { useEffect, useMemo, useRef, useState } from 'react';
import {
  useSprintsStore,
  selectIterations,
  selectAreas,
  selectPrefixes,
} from '@/state/useSprintsStore';
import { useProductsStore } from '@/state/useProductsStore';
import { fmtDate, fmtDateRange } from '@/sprints/sampleSprintData';
import { forgeApi } from '@/api/forgeApi';
import type {
  ProductIteration,
  ProductAreaPath,
  SavedPrefix,
  DiscoveryNode,
  IterationState,
  Repo,
} from '@/types';

type ManagerTab = 'iterations' | 'areas' | 'import' | 'prefixes';

interface SprintManagerProps {
  open: boolean;
  productId: string;
  productName: string;
  initialTab?: ManagerTab;
  initialPrefix?: string;
  onClose: () => void;
}

export function SprintManager({
  open, productId, productName, initialTab = 'iterations',
  initialPrefix = '', onClose,
}: SprintManagerProps) {
  const [tab, setTab] = useState<ManagerTab>(initialTab);
  // Reset tab + prefix when re-opened so deep-links work consistently.
  useEffect(() => { if (open) setTab(initialTab); }, [open, initialTab]);

  // ── Store-backed reads + lazy ensure ─────────────────────────────────
  const ensure = useSprintsStore((s) => s.ensure);
  useEffect(() => { if (open) void ensure(productId); }, [open, ensure, productId]);

  const iterations = useSprintsStore(selectIterations(productId));
  const areas      = useSprintsStore(selectAreas(productId));
  const prefixes   = useSprintsStore(selectPrefixes(productId));
  const error      = useSprintsStore((s) => s.byProduct[productId]?.error ?? null);

  // ── Repos for this product (from the products store, not local mock) ─
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  useEffect(() => { if (open) void loadReposFor(productId); }, [open, loadReposFor, productId]);
  const productRepos = reposByProduct[productId] ?? [];

  // ── Mutations — go through the store so the cache stays in sync ──────
  const importIters = useSprintsStore((s) => s.importIterations);
  const importAreas = useSprintsStore((s) => s.importAreaPaths);
  const toggleDefault = useSprintsStore((s) => s.toggleDefaultArea);
  const savePrefixApi = useSprintsStore((s) => s.savePrefix);
  const deletePrefix = useSprintsStore((s) => s.deletePrefix);
  const refreshPrefix = useSprintsStore((s) => s.refreshPrefix);
  const invalidate = useSprintsStore((s) => s.invalidate);

  const [busy, setBusy] = useState(false);
  const [opError, setOpError] = useState<string | null>(null);

  async function runOp<T>(fn: () => Promise<T>): Promise<T | null> {
    setBusy(true);
    setOpError(null);
    try { return await fn(); }
    catch (err) {
      setOpError(err instanceof Error ? err.message : String(err));
      return null;
    }
    finally { setBusy(false); }
  }

  if (!open) return null;

  return (
    <div className="sprint-modal-backdrop" onClick={onClose} role="dialog" aria-modal="true" aria-labelledby="sprint-manager-title">
      <div className="sprint-modal" onClick={(e) => e.stopPropagation()}>
        <header className="sprint-modal-head">
          <div>
            <div className="eyebrow">15 · sprint &amp; area path management</div>
            <h2 id="sprint-manager-title">{productName} <span style={{ color: 'var(--text-tertiary)', fontWeight: 400 }}>· iteration registry</span></h2>
          </div>
          <div className="actions">
            <button className="icon-x" onClick={onClose} aria-label="Close">
              <svg width="14" height="14" viewBox="0 0 12 12" fill="none">
                <path d="M3 3 L 9 9 M 9 3 L 3 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
              </svg>
            </button>
          </div>
        </header>

        <nav className="sprint-modal-tabs" role="tablist">
          <TabBtn active={tab === 'iterations'} onClick={() => setTab('iterations')}>
            Iterations <span className="badge">{iterations.length}</span>
          </TabBtn>
          <TabBtn active={tab === 'areas'} onClick={() => setTab('areas')}>
            Area paths <span className="badge">{areas.length}</span>
          </TabBtn>
          <TabBtn active={tab === 'import'} onClick={() => setTab('import')}>
            Import from ADO
          </TabBtn>
          <TabBtn active={tab === 'prefixes'} onClick={() => setTab('prefixes')}>
            Saved prefixes <span className="badge">{prefixes.length}</span>
          </TabBtn>
        </nav>

        <div className="sprint-modal-body">
          {(error || opError) && (
            <div className="helper-banner" style={{ background: 'var(--danger-bg)', color: 'var(--danger)', borderColor: 'var(--danger-border)', marginBottom: 12 }}>
              <strong>Request failed:</strong> {opError ?? error}
            </div>
          )}

          {tab === 'iterations' && (
            <IterationsTab
              iterations={iterations}
              prefixes={prefixes.filter((p) => p.kind === 'iterations')}
              onJumpToImport={() => setTab('import')}
            />
          )}
          {tab === 'areas' && (
            <AreasTab
              areas={areas}
              prefixes={prefixes.filter((p) => p.kind === 'areas')}
              onToggleDefault={(id) => void runOp(() => toggleDefault(productId, id))}
            />
          )}
          {tab === 'import' && (
            <ImportTab
              productId={productId}
              productRepos={productRepos}
              initialPrefix={initialPrefix}
              alreadyImportedIterations={new Set(iterations.map((i) => i.providerNodeId))}
              alreadyImportedAreas={new Set(areas.map((a) => a.providerNodeId))}
              busy={busy}
              onImport={async (kind, repoId, nodes, savedFromPrefix) => {
                await runOp(async () => {
                  if (kind === 'iterations') {
                    await importIters(productId, {
                      repoId,
                      providerNodeIds: nodes.map((n) => n.providerNodeId),
                      savedFromPrefix,
                    });
                  } else {
                    await importAreas(productId, {
                      repoId,
                      providerNodeIds: nodes.map((n) => n.providerNodeId),
                      savedFromPrefix,
                    });
                  }
                });
              }}
              onSavePrefix={async (prefix, kind, repoId) => {
                await runOp(() => savePrefixApi(productId, { prefix, kind, repoId }));
              }}
            />
          )}
          {tab === 'prefixes' && (
            <PrefixesTab
              prefixes={prefixes}
              onRefresh={(p) => void runOp(() => refreshPrefix(productId, p.id))}
              onRemove={(p) => void runOp(() => deletePrefix(productId, p.id))}
            />
          )}
        </div>

        <footer className="sprint-modal-foot">
          <div className="info">
            Last refresh · <strong>{lastRefreshLabel(iterations, prefixes)}</strong> · nightly job @ 02:00 per crew TZ
          </div>
          <div className="right">
            <button onClick={() => void invalidate(productId)} disabled={busy}>Refresh now</button>
            <button className="primary" onClick={onClose}>Done</button>
          </div>
        </footer>
      </div>
    </div>
  );
}

function lastRefreshLabel(iters: ProductIteration[], prefixes: SavedPrefix[]): string {
  const candidates = [
    ...iters.map((i) => i.refreshedAt),
    ...prefixes.map((p) => p.lastRefreshedAt),
  ].filter(Boolean) as string[];
  if (candidates.length === 0) return 'never';
  return new Date(candidates.sort().slice(-1)[0]).toLocaleString();
}

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button data-active={active} onClick={onClick} role="tab" aria-selected={active}>
      {children}
    </button>
  );
}

// ════════════════════════════════════════════════════════════════════
// ITERATIONS TAB
// ════════════════════════════════════════════════════════════════════

function IterationsTab({ iterations, prefixes, onJumpToImport }: {
  iterations: ProductIteration[];
  prefixes: SavedPrefix[];
  onJumpToImport: () => void;
}) {
  const order: Record<IterationState, number> = { current: 0, future: 1, past: 2, 'provider-orphan': 3 };
  const sorted = useMemo(
    () => [...iterations].sort((a, b) => {
      const oa = order[a.state] - order[b.state];
      if (oa !== 0) return oa;
      return (a.startDate ?? '').localeCompare(b.startDate ?? '');
    }),
    [iterations],
  );

  if (iterations.length === 0) {
    return (
      <div className="helper-banner">
        <span>📅</span>
        <div>
          <strong>No iterations imported yet.</strong> Switch to the
          {' '}<button
            type="button"
            onClick={onJumpToImport}
            style={{ background: 'transparent', border: 0, color: 'var(--accent)', cursor: 'pointer', padding: 0, font: 'inherit', textDecoration: 'underline' }}
          >Import from ADO</button>{' '}
          tab and pick a prefix (e.g. <code>Atlas\Sprint</code>) to bring sprints into this product.
        </div>
      </div>
    );
  }

  return (
    <div>
      {prefixes.length > 0 && (
        <div className="prefix-chip-row">
          {prefixes.map((p) => (
            <button key={p.id} className="prefix-chip" onClick={onJumpToImport} title={`Refresh — last run ${fmtDate(p.lastRefreshedAt)}`}>
              <span className="pfx">{p.prefix}</span>
              {p.delta.added > 0 && <span className="delta">+{p.delta.added}</span>}
              <span className="meta">{p.repoName} · {fmtDate(p.lastRefreshedAt)}</span>
            </button>
          ))}
        </div>
      )}

      <div className="iter-list">
        {sorted.map((i) => (<IterationRow key={i.id} iter={i} />))}
      </div>

      <div style={{ marginTop: 14, fontSize: 11.5, color: 'var(--text-secondary)' }}>
        <strong style={{ color: 'var(--text-primary)' }}>{sorted.filter((s) => s.state === 'current').length} active</strong>
        {' · '}{sorted.filter((s) => s.state === 'future').length} future
        {' · '}{sorted.filter((s) => s.state === 'past').length} past
        {' · '}{sorted.filter((s) => s.state === 'provider-orphan').length} orphaned
      </div>
    </div>
  );
}

function IterationRow({ iter }: { iter: ProductIteration }) {
  const pct = iter.pointsTotal > 0 ? (iter.pointsDone / iter.pointsTotal) * 100 : 0;
  return (
    <div className="iter-row" data-state={iter.state}>
      <span className="marker" />
      <div className="name-block">
        <div className="name">
          {iter.name}
          {iter.state === 'provider-orphan' && (
            <span className="orphan-pill" style={{ marginLeft: 8 }}>provider-orphan</span>
          )}
        </div>
        <div className="path"><PathSegments path={iter.path} /></div>
      </div>
      <div className="dates">{fmtDateRange(iter.startDate, iter.finishDate)}</div>
      <div className="progress">
        <div className="bar"><span className="fill" style={{ width: `${pct}%` }} /></div>
        <div className="lbl">
          {iter.pointsTotal === 0 ? 'no items yet' : `${iter.pointsDone} / ${iter.pointsTotal} pts`}
        </div>
      </div>
      <div className="bindings">
        {iter.bindings.length === 0
          ? <span style={{ fontFamily: 'var(--font-mono)', fontSize: 9.5, color: 'var(--text-tertiary)' }}>unbound</span>
          : iter.bindings.map((b, i) => (
            <span key={i} className="binding-pill" data-role={b.role}>
              <span>{b.repoName}</span>
              <span className="role">{b.role}</span>
            </span>
          ))}
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// AREAS TAB
// ════════════════════════════════════════════════════════════════════

function AreasTab({ areas, prefixes, onToggleDefault }: {
  areas: ProductAreaPath[];
  prefixes: SavedPrefix[];
  onToggleDefault: (id: string) => void;
}) {
  const sorted = useMemo(() => [...areas].sort((a, b) => a.path.localeCompare(b.path)), [areas]);

  if (areas.length === 0) {
    return (
      <div className="helper-banner">
        <span>🗂</span>
        <div>
          <strong>No area paths imported yet.</strong> Open the Import tab,
          flip to the <em>Area paths</em> mode, and import nodes under a
          prefix like <code>Atlas\Updates</code>.
        </div>
      </div>
    );
  }

  return (
    <div>
      {prefixes.length > 0 && (
        <div className="prefix-chip-row">
          {prefixes.map((p) => (
            <button key={p.id} className="prefix-chip" title={`Refresh — last run ${fmtDate(p.lastRefreshedAt)}`}>
              <span className="pfx">{p.prefix}</span>
              {p.delta.added > 0 && <span className="delta">+{p.delta.added}</span>}
              {p.delta.renamed > 0 && <span className="delta" style={{ background: 'var(--info)' }}>~{p.delta.renamed}</span>}
              <span className="meta">{fmtDate(p.lastRefreshedAt)}</span>
            </button>
          ))}
        </div>
      )}

      <div className="area-tree">
        {sorted.map((a) => {
          const depth = a.path.split('\\').length - 1;
          return (
            <div
              key={a.id}
              className="node"
              data-default={a.isDefaultFilter}
              onClick={() => onToggleDefault(a.id)}
              role="button"
              aria-pressed={a.isDefaultFilter}
              title={a.isDefaultFilter ? 'Unset default filter' : 'Set as default filter'}
            >
              <div className="label" style={{ paddingLeft: depth * 18 }}>
                {depth > 0 && <span className="indent" />}
                <span className="seg-prefix"><PathSegments path={a.path} highlightLast /></span>
              </div>
              <div className="count">{a.workItemCount} item{a.workItemCount === 1 ? '' : 's'}</div>
              <button
                className="default-toggle"
                onClick={(e) => { e.stopPropagation(); onToggleDefault(a.id); }}
              >
                {a.isDefaultFilter ? '★ default' : 'set default'}
              </button>
            </div>
          );
        })}
      </div>

      <div className="helper-banner" style={{ marginTop: 14 }}>
        <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0, marginTop: 1 }}>
          <circle cx="7" cy="7" r="5.5" stroke="currentColor" strokeWidth="1.2" />
          <line x1="7" y1="6" x2="7" y2="10" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          <circle cx="7" cy="4" r="0.9" fill="currentColor" />
        </svg>
        <div>
          <strong>Default area path</strong> applies to Plan &amp; Design and Work items
          before any user filter. Click any row to toggle. ADO repos only — GitHub
          repos use labels for area scoping.
        </div>
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// IMPORT TAB — calls the live discovery endpoint with debounce.
// ════════════════════════════════════════════════════════════════════

function ImportTab({
  productId, productRepos, initialPrefix,
  alreadyImportedIterations, alreadyImportedAreas,
  busy, onImport, onSavePrefix,
}: {
  productId: string;
  productRepos: Repo[];
  initialPrefix: string;
  alreadyImportedIterations: Set<string>;
  alreadyImportedAreas: Set<string>;
  busy: boolean;
  onImport: (
    kind: 'iterations' | 'areas',
    repoId: string,
    nodes: DiscoveryNode[],
    savedFromPrefix: string | undefined,
  ) => Promise<void>;
  onSavePrefix: (prefix: string, kind: 'iterations' | 'areas', repoId: string) => Promise<void>;
}) {
  const [prefix, setPrefix] = useState(initialPrefix);
  const [mode, setMode] = useState<'iterations' | 'areas'>('iterations');
  const [repoId, setRepoId] = useState<string>(productRepos[0]?.id ?? '');
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [savePrefixOn, setSavePrefixOn] = useState(false);

  const [discovery, setDiscovery] = useState<DiscoveryNode[]>([]);
  const [discovering, setDiscovering] = useState(false);
  const [discoveryError, setDiscoveryError] = useState<string | null>(null);

  // Reset selection if the active product's repo set changes.
  useEffect(() => {
    if (productRepos.length === 0) { setRepoId(''); return; }
    if (!productRepos.some((r) => r.id === repoId)) {
      setRepoId(productRepos[0].id);
    }
  }, [productRepos, repoId]);

  const repo = useMemo(
    () => productRepos.find((r) => r.id === repoId) ?? productRepos[0],
    [productRepos, repoId],
  );
  const isGithub = repo?.kind === 'github';

  // Debounced discovery: re-fetch 300ms after the prefix / repo / mode
  // settles. GitHub variant ignores the prefix entirely.
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (!repo) { setDiscovery([]); return; }
    if (!isGithub && prefix.trim() === '') { setDiscovery([]); return; }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(async () => {
      setDiscovering(true);
      setDiscoveryError(null);
      try {
        const nodes = await forgeApi.discoverSprintNodes(productId, {
          repoId: repo.id,
          kind: mode,
          prefix: isGithub ? undefined : prefix.replace(/\\+$/, '').trim(),
        });
        setDiscovery(nodes);
      } catch (err) {
        setDiscoveryError(err instanceof Error ? err.message : String(err));
        setDiscovery([]);
      } finally {
        setDiscovering(false);
      }
    }, 300);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [productId, prefix, mode, repo, isGithub]);

  const importedSet = mode === 'iterations' ? alreadyImportedIterations : alreadyImportedAreas;
  const newCount = discovery.filter((n) => !importedSet.has(n.providerNodeId)).length;
  const checkedNew = useMemo(
    () => discovery.filter((n) => checked.has(n.providerNodeId) && !importedSet.has(n.providerNodeId)),
    [discovery, checked, importedSet],
  );

  function toggle(id: string) {
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  function selectAllNew() {
    const next = new Set(checked);
    discovery.forEach((n) => { if (!importedSet.has(n.providerNodeId)) next.add(n.providerNodeId); });
    setChecked(next);
  }

  async function apply() {
    if (checkedNew.length === 0 || !repo) return;
    const savedFromPrefix = savePrefixOn && prefix.trim() ? prefix.trim() : undefined;
    await onImport(mode, repo.id, checkedNew, savedFromPrefix);
    if (savedFromPrefix) {
      // Best-effort save — server will 409 if duplicate, swallowed by runOp.
      await onSavePrefix(savedFromPrefix, mode, repo.id).catch(() => {});
    }
    setChecked(new Set());
    setSavePrefixOn(false);
  }

  if (productRepos.length === 0 || !repo) {
    return (
      <div className="helper-banner">
        <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0, marginTop: 1 }}>
          <circle cx="7" cy="7" r="5.5" stroke="currentColor" strokeWidth="1.2" />
          <line x1="7" y1="6" x2="7" y2="10" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          <circle cx="7" cy="4" r="0.9" fill="currentColor" />
        </svg>
        <div>
          <strong>This product has no repos attached.</strong> Attach a repo
          on the <code>Products</code> page first, then come back to import
          iteration / area paths from its provider.
        </div>
      </div>
    );
  }

  const repoOrg = repo.adoOrg ?? repo.ghOwner ?? '';
  const repoProject = repo.adoProject ?? repo.ghRepo ?? '';

  return (
    <div className="import-stage">
      <div className="prefix-console">
        <div className="label">{isGithub ? 'milestones' : `prefix · ${mode}`}</div>
        {isGithub ? (
          <div style={{ padding: '14px 16px', fontFamily: 'var(--font-mono)', fontSize: 13, color: 'var(--text-secondary)' }}>
            GitHub milestones are flat — listing all milestones for{' '}
            <strong style={{ color: 'var(--text-primary)' }}>{repoOrg}/{repoProject}</strong>.
          </div>
        ) : (
          <input
            value={prefix}
            onChange={(e) => setPrefix(e.target.value)}
            placeholder="Atlas\Sprint   or   Atlas\Releases\v1"
            spellCheck={false}
            autoFocus
          />
        )}
        <div className="repo-pick">
          <label style={{ fontSize: 9.5, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.1 }}>repo</label>
          <select value={repoId} onChange={(e) => { setRepoId(e.target.value); setChecked(new Set()); }}>
            {productRepos.map((r) => (
              <option key={r.id} value={r.id}>{r.displayName} · {r.kind.toUpperCase()}</option>
            ))}
          </select>
        </div>
      </div>

      {!isGithub && (
        <div className="import-mode-tabs" role="tablist">
          {(['iterations', 'areas'] as const).map((m) => (
            <button
              key={m}
              data-active={mode === m}
              onClick={() => { setMode(m); setChecked(new Set()); }}
              role="tab"
              aria-selected={mode === m}
            >
              {m === 'iterations' ? 'Iterations' : 'Area paths'}
            </button>
          ))}
        </div>
      )}

      <div>
        {discoveryError ? (
          <div className="helper-banner" style={{ background: 'var(--danger-bg)', color: 'var(--danger)', borderColor: 'var(--danger-border)' }}>
            <strong>Discovery failed:</strong> {discoveryError}
          </div>
        ) : prefix.trim() === '' && !isGithub ? (
          <div className="helper-banner">
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0, marginTop: 1 }}>
              <circle cx="7" cy="7" r="5.5" stroke="currentColor" strokeWidth="1.2" />
              <line x1="7" y1="6" x2="7" y2="10" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
              <circle cx="7" cy="4" r="0.9" fill="currentColor" />
            </svg>
            <div>
              <strong>Type a path prefix</strong> to start. Forge will call ADO's
              classification node API and preview the matching subtree below. Try
              {' '}<code>Atlas\Sprint</code>{' '}or{' '}<code>Atlas\Releases\v1</code>.
            </div>
          </div>
        ) : (
          <>
            <div className="import-status" style={{ marginBottom: 10 }}>
              <span className="live">
                {discovering ? 'fetching…' : (isGithub ? 'live · github milestones' : 'live · ado classificationnodes')}
              </span>
              <span><span className="num">{discovery.length}</span> matching</span>
              <span><span className="num" style={{ color: 'var(--success)' }}>{newCount}</span> new</span>
              <span><span className="num" style={{ color: 'var(--text-tertiary)' }}>{discovery.length - newCount}</span> already imported</span>
              {newCount > 0 && (
                <button
                  onClick={selectAllNew}
                  style={{
                    marginLeft: 'auto', background: 'transparent',
                    border: '1px solid var(--border-strong)',
                    fontFamily: 'var(--font-mono)', fontSize: 10,
                    letterSpacing: 0.06, textTransform: 'uppercase',
                    padding: '4px 9px', borderRadius: 4,
                    cursor: 'pointer', color: 'var(--text-primary)',
                  }}
                >
                  select all new
                </button>
              )}
            </div>

            <div className="discovery-tree">
              {!discovering && discovery.length === 0 ? (
                <div style={{ padding: 24, textAlign: 'center', fontFamily: 'var(--font-mono)', fontSize: 11, color: 'var(--text-tertiary)' }}>
                  No nodes match <code>{prefix}</code> in <strong>{repoOrg}/{repoProject}</strong>.
                </div>
              ) : (
                discovery.map((node) => {
                  const imported = importedSet.has(node.providerNodeId) || node.alreadyImported;
                  const isChecked = checked.has(node.providerNodeId);
                  return (
                    <div
                      key={node.providerNodeId}
                      className="discovery-row"
                      data-checked={isChecked}
                      data-imported={imported}
                      onClick={() => { if (!imported) toggle(node.providerNodeId); }}
                    >
                      <span className="check">
                        {(isChecked || imported) && (
                          <svg width="10" height="10" viewBox="0 0 12 12" fill="none">
                            <path d="M2.5 6.5 L 5 9 L 9.5 3.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
                          </svg>
                        )}
                      </span>
                      <span className="glyph">{node.hasChildren ? '┳' : '─'}</span>
                      <span className="path-cell" style={{ paddingLeft: node.depth * 14 }}>
                        <PathSegments path={node.path} highlightLast />
                      </span>
                      {node.attributes.timeFrame && (
                        <span className="timeframe" data-tf={node.attributes.timeFrame}>
                          {node.attributes.timeFrame}
                        </span>
                      )}
                      {mode === 'iterations' && node.attributes.startDate && (
                        <span className="dates">
                          {fmtDate(node.attributes.startDate)} – {fmtDate(node.attributes.finishDate ?? null)}
                        </span>
                      )}
                      {mode === 'areas' && node.workItemCount !== undefined && (
                        <span className="dates">{node.workItemCount} items</span>
                      )}
                      {imported && <span className="imported-note">imported</span>}
                    </div>
                  );
                })
              )}
            </div>

            {newCount > 0 && (
              <div
                className="save-prefix-row"
                data-on={savePrefixOn}
                onClick={() => setSavePrefixOn((v) => !v)}
                style={{ marginTop: 14, cursor: 'pointer' }}
                role="checkbox"
                aria-checked={savePrefixOn}
              >
                <div className="toggle" />
                <div className="label">
                  Save <span className="strong">{prefix.trim() || '(prefix)'}</span> as a shorthand on this product —
                  one-click refresh on next run, with a <span className="strong">+N</span> badge when new nodes appear.
                </div>
              </div>
            )}
          </>
        )}
      </div>

      <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end', marginTop: 14 }}>
        <button
          onClick={() => setChecked(new Set())}
          disabled={checked.size === 0}
          style={{
            fontFamily: 'var(--font-mono)', fontSize: 10.5, letterSpacing: 0.05,
            textTransform: 'uppercase', padding: '7px 14px',
            borderRadius: 'var(--radius-md)', border: '1px solid var(--border-strong)',
            background: 'var(--bg-surface)',
            cursor: checked.size === 0 ? 'not-allowed' : 'pointer',
            opacity: checked.size === 0 ? 0.4 : 1,
            color: 'var(--text-primary)',
          }}
        >
          Clear
        </button>
        <button
          onClick={() => void apply()}
          disabled={checkedNew.length === 0 || busy}
          style={{
            fontFamily: 'var(--font-mono)', fontSize: 10.5, letterSpacing: 0.05,
            textTransform: 'uppercase', padding: '7px 16px',
            borderRadius: 'var(--radius-md)', border: '1px solid var(--text-primary)',
            background: 'var(--text-primary)', color: 'var(--bg-surface)',
            cursor: (checkedNew.length === 0 || busy) ? 'not-allowed' : 'pointer',
            opacity: (checkedNew.length === 0 || busy) ? 0.4 : 1,
          }}
        >
          {busy
            ? 'Importing…'
            : `Import ${checkedNew.length > 0 ? `${checkedNew.length} ${mode === 'iterations' ? 'iteration' : 'area'}${checkedNew.length === 1 ? '' : 's'}` : ''}`}
        </button>
      </div>
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// SAVED PREFIXES TAB
// ════════════════════════════════════════════════════════════════════

function PrefixesTab({ prefixes, onRefresh, onRemove }: {
  prefixes: SavedPrefix[];
  onRefresh: (p: SavedPrefix) => void;
  onRemove: (p: SavedPrefix) => void;
}) {
  if (prefixes.length === 0) {
    return (
      <div className="helper-banner">
        <svg width="14" height="14" viewBox="0 0 14 14" fill="none" style={{ flexShrink: 0, marginTop: 1 }}>
          <circle cx="7" cy="7" r="5.5" stroke="currentColor" strokeWidth="1.2" />
          <line x1="7" y1="6" x2="7" y2="10" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          <circle cx="7" cy="4" r="0.9" fill="currentColor" />
        </svg>
        <div>
          <strong>No saved prefixes yet.</strong> Run an import — there's a{' '}
          "Save as shorthand" toggle on the import screen. Saved prefixes give
          you one-click refresh and surface a <code>+N</code> badge when new
          nodes appear upstream.
        </div>
      </div>
    );
  }

  return (
    <div className="saved-prefix-list">
      {prefixes.map((p) => (
        <div key={p.id} className="saved-prefix-row">
          <div>
            <span className="pfx">{p.prefix}<span className="kind">{p.kind}</span></span>
            <div style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)', marginTop: 4 }}>
              repo · {p.repoName} · added by {p.addedBy} · last run {fmtDate(p.lastRefreshedAt)}
            </div>
          </div>
          <div className="delta-block">
            {p.delta.added > 0 && <span className="add">+{p.delta.added} added</span>}
            {p.delta.removed > 0 && <span className="rm">−{p.delta.removed} removed</span>}
            {p.delta.renamed > 0 && <span className="mv">~{p.delta.renamed} renamed</span>}
            {p.delta.added === 0 && p.delta.removed === 0 && p.delta.renamed === 0 && (
              <span style={{ color: 'var(--text-tertiary)' }}>no change</span>
            )}
          </div>
          <div style={{ display: 'flex', gap: 6 }}>
            <button onClick={() => onRefresh(p)}>Refresh</button>
            <button onClick={() => onRemove(p)} style={{ color: 'var(--danger)' }}>Remove</button>
          </div>
        </div>
      ))}
    </div>
  );
}

// ════════════════════════════════════════════════════════════════════
// Path segment rendering — colored breadcrumbs
// ════════════════════════════════════════════════════════════════════

function PathSegments({ path, highlightLast = false }: { path: string; highlightLast?: boolean }) {
  const segs = path.split('\\');
  return (
    <>
      {segs.map((s, i) => (
        <span key={i}>
          {i > 0 && <span className="sep">\</span>}
          <span className={highlightLast && i === segs.length - 1 ? 'leaf' : 'seg'}>
            {s}
          </span>
        </span>
      ))}
    </>
  );
}
