// Phase 108 — Rules & guardrails: the product rule catalog.
//
// A rule is a standing directive that goes into an agent's generator prompt
// on every iteration of the work items it reaches. It reaches a work item in
// one of two ways:
//   - tagged: one of its tags matches the work item's repo and text, so it is
//     added automatically (lane 01);
//   - hand-picked: the operator ticks it under "Rules for this work item" on
//     the work item's execution page or a workstream's Overview (lane 02).
// Tags (lane 03) are the matchers. Data: /api/products/{id}/rule-catalog.

import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import {
  forgeApi,
  type CatalogRule,
  type CatalogRuleInput,
  type RuleCatalog,
  type RuleTag,
  type RuleTagInput,
  type RuleTagMatch,
} from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useProductsStore } from '@/state/useProductsStore';
import { RuleEditor, TagEditor } from '@/swarm/RuleCatalogView';
import { emptyTag, rulesUsingTag, tagMatchers, tagNames, tagScope } from '@/swarm/ruleCatalogText';

type Editor =
  | { kind: 'rule'; id: string | null; value: CatalogRuleInput }
  | { kind: 'tag'; id: string | null; value: RuleTagInput };

const newRule = (tagIds: string[] = []): CatalogRuleInput => ({ title: '', text: '', tagIds, enabled: true });

export function PoliciesView() {
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const productId = activeProductId && activeProductId !== 'all' ? activeProductId : null;
  const [catalog, setCatalog] = useState<RuleCatalog | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const [matches, setMatches] = useState<{ tagId: string; rows: RuleTagMatch[] } | null>(null);

  const load = useCallback(async () => {
    if (!productId) return;
    try {
      setCatalog(await forgeApi.getRuleCatalog(productId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Loading the rules failed');
    }
  }, [productId]);
  useEffect(() => { setCatalog(null); void load(); }, [load]);

  async function run(action: () => Promise<unknown>, after?: () => void) {
    setBusy(true);
    setError(null);
    try {
      await action();
      after?.();
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The change failed');
    } finally {
      setBusy(false);
    }
  }

  const ask = (title: string, body: string) =>
    useConfirmStore.getState().ask({ title, body, tone: 'warn', confirmLabel: 'Delete', cancelLabel: 'Keep' });

  const counts = useMemo(() => {
    const rules = catalog?.rules ?? [];
    const attached = Object.values(catalog?.usage ?? {}).reduce((s, u) => s + u.attached, 0);
    return {
      total: rules.length,
      enabled: rules.filter((r) => r.enabled).length,
      tagged: rules.filter((r) => r.tagIds.length > 0).length,
      handPicked: rules.filter((r) => r.tagIds.length === 0).length,
      tags: catalog?.tags.length ?? 0,
      attached,
    };
  }, [catalog]);

  function exportCatalog() {
    if (!catalog) return;
    const blob = new Blob([JSON.stringify({ productId: catalog.productId, tags: catalog.tags, rules: catalog.rules }, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `rules-${catalog.productId}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  function saveEditor(value: CatalogRuleInput | RuleTagInput) {
    if (!productId || !editor) return;
    const ed = editor;
    void run(() => {
      if (ed.kind === 'rule') {
        const v = value as CatalogRuleInput;
        return ed.id ? forgeApi.updateCatalogRule(productId, ed.id, v) : forgeApi.addCatalogRule(productId, v);
      }
      const v = value as RuleTagInput;
      return ed.id ? forgeApi.updateRuleTag(productId, ed.id, v) : forgeApi.addRuleTag(productId, v);
    }, () => { setEditor(null); setMatches(null); });
  }

  const tagged = catalog?.rules.filter((r) => r.tagIds.length > 0) ?? [];
  const handPicked = catalog?.rules.filter((r) => r.tagIds.length === 0) ?? [];

  return (
    <section className="view active" id="view-policies">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">08</span>&nbsp;Policies</span>
            <span className="sep">/</span>
            <span>{counts.enabled}/{counts.total} rules enabled · {counts.tags} tags</span>
            <span className="sep">/</span>
            <span>product {productId ?? '—'}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Rules &amp; guardrails
          </h1>
        </div>
        <div className="page-head-right">
          <button type="button" className="btn btn-secondary" disabled={!catalog} onClick={exportCatalog}>Export JSON</button>
          <button type="button" className="btn btn-primary" disabled={!catalog || busy}
            onClick={() => setEditor({ kind: 'rule', id: null, value: newRule() })}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New rule
          </button>
        </div>
      </div>

      {!productId && <p className="policies-note">Choose a product in the top bar to see its rules.</p>}
      {error && <p className="policies-error" role="alert">{error}</p>}

      {catalog && (
        <>
          <div className="workstreams-stats" style={{ margin: '0 28px 12px' }}>
            <div className="stat-cell">
              <div className="stat-cell-label">Active rules</div>
              <div className="stat-cell-val">{counts.enabled}<span className="unit">of {counts.total}</span></div>
              <div className="stat-cell-trend">{counts.total - counts.enabled} disabled</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Tagged</div>
              <div className="stat-cell-val">{counts.tagged}</div>
              <div className="stat-cell-trend">added automatically where a tag matches</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Hand-picked</div>
              <div className="stat-cell-val">{counts.handPicked}</div>
              <div className="stat-cell-trend">added only where you tick them</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Attached by hand</div>
              <div className="stat-cell-val">{counts.attached}</div>
              <div className="stat-cell-trend">work items with a rule ticked</div>
            </div>
          </div>

          <ol className="policies-howto">
            <li><strong>Write a rule</strong> once: what the agent must do. It goes into the generator prompt from each work item&apos;s next iteration.</li>
            <li><strong>Attach it by hand</strong>: open a work item&apos;s execution page or a workstream&apos;s Overview and tick it under <em>Rules for this work item</em>.</li>
            <li><strong>Or give it a tag</strong> to add it automatically to every work item whose repo and text the tag matches.</li>
          </ol>

          <div className="policies-wrap">
            <Lane num="01" title="Tagged rules" sub="Added automatically to every work item one of their tags matches"
              count={tagged.length} addLabel="+ new tagged rule"
              onAdd={() => setEditor({ kind: 'rule', id: null, value: newRule(catalog.tags[0] ? [catalog.tags[0].id] : []) })}>
              {tagged.length === 0 && <p className="policies-empty">No tagged rules. Give a rule a tag to add it automatically.</p>}
              {tagged.map((r) => (
                <RuleCard key={r.id} rule={r} catalog={catalog} busy={busy}
                  onToggle={() => void run(() => forgeApi.updateCatalogRule(productId!, r.id, { ...r, enabled: !r.enabled }))}
                  onEdit={() => setEditor({ kind: 'rule', id: r.id, value: r })}
                  onDelete={() => void (async () => {
                    if (await ask('DELETE THIS RULE?', `${r.title} — it leaves every work item it reaches, from their next iteration.`)) {
                      await run(() => forgeApi.deleteCatalogRule(productId!, r.id));
                    }
                  })()} />
              ))}
            </Lane>

            <Lane num="02" title="Hand-picked rules" sub="Added only to the work items you tick them on"
              count={handPicked.length} addLabel="+ new hand-picked rule"
              onAdd={() => setEditor({ kind: 'rule', id: null, value: newRule() })}>
              {handPicked.length === 0 && <p className="policies-empty">No hand-picked rules yet.</p>}
              {handPicked.map((r) => (
                <RuleCard key={r.id} rule={r} catalog={catalog} busy={busy}
                  onToggle={() => void run(() => forgeApi.updateCatalogRule(productId!, r.id, { ...r, enabled: !r.enabled }))}
                  onEdit={() => setEditor({ kind: 'rule', id: r.id, value: r })}
                  onDelete={() => void (async () => {
                    if (await ask('DELETE THIS RULE?', `${r.title} — it leaves every work item it is ticked on, from their next iteration.`)) {
                      await run(() => forgeApi.deleteCatalogRule(productId!, r.id));
                    }
                  })()} />
              ))}
            </Lane>

            <Lane num="03" title="Tags" sub="Match work items by repo and text; rules on a tag follow it"
              count={catalog.tags.length} addLabel="+ new tag"
              onAdd={() => setEditor({ kind: 'tag', id: null, value: emptyTag })}>
              {catalog.tags.length === 0 && <p className="policies-empty">No tags yet. A tag is optional; it attaches rules automatically.</p>}
              {catalog.tags.map((t) => (
                <TagCard key={t.id} tag={t} catalog={catalog} busy={busy}
                  matches={matches?.tagId === t.id ? matches.rows : null}
                  onCheck={() => void run(async () => setMatches({ tagId: t.id, rows: await forgeApi.listRuleTagMatches(productId!, t.id) }))}
                  onEdit={() => setEditor({ kind: 'tag', id: t.id, value: t })}
                  onDelete={() => void (async () => {
                    const used = rulesUsingTag(catalog.rules, t.id).length;
                    if (await ask('DELETE THIS TAG?', `${t.name} — ${used} rule(s) lose it and stop reaching the work items it matched, unless ticked there by hand.`)) {
                      await run(() => forgeApi.deleteRuleTag(productId!, t.id), () => setMatches(null));
                    }
                  })()} />
              ))}
            </Lane>
          </div>
        </>
      )}

      {editor && catalog && (
        <EditorDialog title={`${editor.id ? 'EDIT' : 'NEW'} ${editor.kind === 'rule' ? 'RULE' : 'TAG'}`} error={error} onClose={() => setEditor(null)}>
          {editor.kind === 'rule'
            ? <RuleEditor initial={editor.value} tags={catalog.tags} busy={busy} onCancel={() => setEditor(null)} onSave={saveEditor} />
            : <TagEditor initial={editor.value} busy={busy} onCancel={() => setEditor(null)} onSave={saveEditor} />}
        </EditorDialog>
      )}
    </section>
  );
}

function Lane({ num, title, sub, count, addLabel, onAdd, children }: {
  num: string; title: string; sub: string; count: number; addLabel: string; onAdd: () => void; children: ReactNode;
}) {
  return (
    <div className="policies-lane">
      <div className="policies-lane-head">
        <h2 className="policies-lane-title">
          <span className="num">{num}</span>
          <span>{title}</span>
        </h2>
        <span className="policies-lane-count">{count}</span>
      </div>
      <p className="policies-lane-sub">{sub}</p>
      {children}
      <button type="button" className="policies-lane-add" onClick={onAdd}>{addLabel}</button>
    </div>
  );
}

/** A rule as a policy card: kind, title, text, what attaches it, and where it is ticked by hand. */
export function RuleCard({ rule, catalog, busy, onToggle, onEdit, onDelete }: {
  rule: CatalogRule;
  catalog: RuleCatalog;
  busy?: boolean;
  onToggle?: () => void;
  onEdit?: () => void;
  onDelete?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const tagged = rule.tagIds.length > 0;
  const usage = catalog.usage?.[rule.id] ?? { attached: 0, detached: 0 };
  const long = rule.text.length > 280 || rule.text.split('\n').length > 5;
  return (
    <article className="policy-card" data-disabled={!rule.enabled || undefined} data-kind={tagged ? 'tagged' : 'manual'}>
      <div className="policy-card-head">
        <div style={{ minWidth: 0 }}>
          <div className="policy-card-eyebrow">
            <span>Rule</span>
            <span className="sep">·</span>
            <span className={`severity ${tagged ? 'info' : 'warn'}`}>{tagged ? 'auto' : 'by hand'}</span>
            {!rule.enabled && <><span className="sep">·</span><span>disabled</span></>}
          </div>
          <h3 className="policy-card-title">{rule.title}</h3>
        </div>
        {onToggle && (
          <button type="button" className="policy-toggle" data-on={rule.enabled || undefined} disabled={busy}
            aria-pressed={rule.enabled} aria-label={`${rule.title}: ${rule.enabled ? 'enabled, click to disable' : 'disabled, click to enable'}`}
            onClick={onToggle} />
        )}
      </div>

      <div className={`policy-card-body policy-card-body--rule${open || !long ? '' : ' is-clamped'}`}>{rule.text}</div>
      {long && (
        <button type="button" className="policy-card-more" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Show less' : 'Show all'}
        </button>
      )}

      <div className="policy-card-trigger">
        <span className="key">when</span>{' '}
        <span className="op">{tagged ? `tag ${tagNames(catalog.tags, rule.tagIds).join(' or ')} matches` : 'ticked on a work item'}</span>
      </div>

      <div className="policy-card-foot">
        <span>
          ticked on <span className="num">{usage.attached}</span>
          {usage.detached > 0 && <> · unticked on <span className="num">{usage.detached}</span></>}
        </span>
        <span className="policy-card-actions">
          {onEdit && <button type="button" disabled={busy} onClick={onEdit}>Edit</button>}
          {onDelete && <button type="button" className="danger" disabled={busy} onClick={onDelete}>Delete</button>}
        </span>
      </div>
    </article>
  );
}

/** A tag as a policy card: scope, matchers, rules on it, and (once checked) the work items it matches. */
export function TagCard({ tag, catalog, busy, matches, onCheck, onEdit, onDelete }: {
  tag: RuleTag;
  catalog: RuleCatalog;
  busy?: boolean;
  matches?: RuleTagMatch[] | null;
  onCheck?: () => void;
  onEdit?: () => void;
  onDelete?: () => void;
}) {
  const used = rulesUsingTag(catalog.rules, tag.id);
  const hits = matches?.filter((m) => m.applied) ?? [];
  const terms = [...tag.phrases.map((p) => `"${p}"`), ...tag.patterns.map((p) => `/${p}/`)];
  return (
    <article className="policy-card" data-kind="tag">
      <div className="policy-card-eyebrow">
        <span>Tag</span>
        <span className="sep">·</span>
        <span>{tagScope(tag)}</span>
      </div>
      <h3 className="policy-card-title">{tag.name}</h3>
      {tag.description && <div className="policy-card-body">{tag.description}</div>}
      <div className="policy-card-trigger">
        <span className="key">when</span>{' '}
        <span className="op">{terms.length > 0 ? `text has ${terms.join(' | ')}` : tagMatchers(tag)}</span>
      </div>
      <div className="policy-card-foot">
        <span>{used.length} rule{used.length === 1 ? '' : 's'}{used.length > 0 ? `: ${used.map((r) => r.title).join(', ')}` : ''}</span>
        <span className="policy-card-actions">
          {onCheck && <button type="button" disabled={busy} onClick={onCheck}>Check matches</button>}
          {onEdit && <button type="button" disabled={busy} onClick={onEdit}>Edit</button>}
          {onDelete && <button type="button" className="danger" disabled={busy} onClick={onDelete}>Delete</button>}
        </span>
      </div>
      {matches && (
        <div className="policy-card-matches">
          <div>Matches {hits.length} of {matches.length} workstreams in this product</div>
          {hits.length > 0 && (
            <ul>
              {hits.slice(0, 50).map((m) => (
                <li key={m.workstreamId} title={m.workstreamId}>
                  <strong>{m.name}</strong> · {m.category} · {m.status} · {m.reason}
                </li>
              ))}
              {hits.length > 50 && <li>and {hits.length - 50} more</li>}
            </ul>
          )}
        </div>
      )}
    </article>
  );
}

function EditorDialog({ title, error, onClose, children }: {
  title: string; error: string | null; onClose: () => void; children: ReactNode;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  // The layer carries `swarm-ctrl` for the editor's theme tokens.
  return createPortal(
    <div className="swarm-ctrl sc-rules-layer" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="sc-rules" role="dialog" aria-modal="true" aria-label={title}>
        <header className="sc-rules__head">
          <h2 className="sc-rules__title">{title}</h2>
          <button type="button" className="sc-rules__close" aria-label="Close" onClick={onClose}>✕</button>
        </header>
        <div className="sc-rules__body">
          {error && <p className="sc-rules__error" role="alert">{error}</p>}
          {children}
        </div>
      </div>
    </div>,
    document.body,
  );
}