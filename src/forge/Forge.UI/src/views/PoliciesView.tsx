// Phase 108 — Rules & guardrails: the product's policies, rules and tags.
//
// A rule is one directive the agent follows on every iteration of the work
// items it reaches. A policy groups rules (a rule can be in several). A tag
// matches work items by repo and text and applies policies to them
// automatically. On one work item (its execution page, or a workstream's
// Overview) the operator can pick or remove a whole policy or a single rule.
// Data: /api/products/{id}/rule-catalog.

import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  forgeApi,
  type CatalogRule,
  type CatalogRuleInput,
  type RuleCatalog,
  type RulePolicy,
  type RulePolicyInput,
  type RuleTag,
  type RuleTagInput,
  type RuleTagMatch,
} from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useProductsStore } from '@/state/useProductsStore';
import { emptyTag, namesOf, policiesWithRule, policiesWithTag, tagMatchers, tagScope } from '@/swarm/ruleCatalogText';
import { Dialog, PolicyForm, RuleForm, TagForm } from './policyEditors';

type Editor =
  | { kind: 'rule'; id: string | null; value: CatalogRuleInput & { policyIds: string[] } }
  | { kind: 'policy'; id: string | null; value: RulePolicyInput }
  | { kind: 'tag'; id: string | null; value: RuleTagInput };

const NEW_RULE = { title: '', text: '', enabled: true, policyIds: [] as string[] };
const NEW_POLICY: RulePolicyInput = { name: '', description: '', ruleIds: [], tagIds: [], enabled: true };

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
      setError(e instanceof Error ? e.message : 'Loading the policies failed');
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

  const confirmDelete = (title: string, body: string) =>
    useConfirmStore.getState().ask({ title, body, tone: 'warn', confirmLabel: 'Delete', cancelLabel: 'Keep' });

  const counts = useMemo(() => {
    const sum = (u: Record<string, { attached: number }> | undefined) => Object.values(u ?? {}).reduce((s, x) => s + x.attached, 0);
    return {
      policies: catalog?.policies.length ?? 0,
      policiesOn: catalog?.policies.filter((p) => p.enabled).length ?? 0,
      rules: catalog?.rules.length ?? 0,
      rulesOn: catalog?.rules.filter((r) => r.enabled).length ?? 0,
      tags: catalog?.tags.length ?? 0,
      picked: sum(catalog?.policyUsage) + sum(catalog?.ruleUsage),
    };
  }, [catalog]);

  function exportCatalog() {
    if (!catalog) return;
    const { productId: pid, policies, rules, tags } = catalog;
    const blob = new Blob([JSON.stringify({ productId: pid, policies, rules, tags }, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `policies-${pid}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  function save(value: CatalogRuleInput | RulePolicyInput | RuleTagInput) {
    if (!productId || !editor) return;
    const ed = editor;
    void run(() => {
      switch (ed.kind) {
        case 'rule': {
          const v = value as CatalogRuleInput;
          return ed.id ? forgeApi.updateCatalogRule(productId, ed.id, v) : forgeApi.addCatalogRule(productId, v);
        }
        case 'policy': {
          const v = value as RulePolicyInput;
          return ed.id ? forgeApi.updateRulePolicy(productId, ed.id, v) : forgeApi.addRulePolicy(productId, v);
        }
        default: {
          const v = value as RuleTagInput;
          return ed.id ? forgeApi.updateRuleTag(productId, ed.id, v) : forgeApi.addRuleTag(productId, v);
        }
      }
    }, () => { setEditor(null); setMatches(null); });
  }

  const editRule = (r: CatalogRule) => catalog && setEditor({
    kind: 'rule', id: r.id, value: { ...r, policyIds: policiesWithRule(catalog, r.id).map((p) => p.id) },
  });

  return (
    <section className="view active" id="view-policies">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">08</span>&nbsp;Policies</span>
            <span className="sep">/</span>
            <span>{counts.policiesOn}/{counts.policies} policies · {counts.rulesOn}/{counts.rules} rules enabled</span>
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
          <button type="button" className="btn btn-secondary" disabled={!catalog || busy}
            onClick={() => setEditor({ kind: 'rule', id: null, value: NEW_RULE })}>New rule</button>
          <button type="button" className="btn btn-primary" disabled={!catalog || busy}
            onClick={() => setEditor({ kind: 'policy', id: null, value: NEW_POLICY })}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New policy
          </button>
        </div>
      </div>

      {!productId && <p className="policies-note">Choose a product in the top bar to see its policies.</p>}
      {error && !editor && <p className="policies-error" role="alert">{error}</p>}

      {catalog && (
        <>
          <div className="workstreams-stats" style={{ margin: '0 28px 12px' }}>
            <div className="stat-cell">
              <div className="stat-cell-label">Policies</div>
              <div className="stat-cell-val">{counts.policiesOn}<span className="unit">of {counts.policies} on</span></div>
              <div className="stat-cell-trend">groups of rules applied together</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Rules</div>
              <div className="stat-cell-val">{counts.rulesOn}<span className="unit">of {counts.rules} on</span></div>
              <div className="stat-cell-trend">directives the agent follows</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Tags</div>
              <div className="stat-cell-val">{counts.tags}</div>
              <div className="stat-cell-trend">apply policies automatically</div>
            </div>
            <div className="stat-cell">
              <div className="stat-cell-label">Picked by hand</div>
              <div className="stat-cell-val">{counts.picked}</div>
              <div className="stat-cell-trend">policy or rule choices on work items</div>
            </div>
          </div>

          <ol className="policies-howto">
            <li><strong>Write rules</strong>: each is one directive the agent follows on every iteration.</li>
            <li><strong>Group them into policies</strong>. A rule can be in several policies.</li>
            <li><strong>Apply</strong> a whole policy or a single rule on a work item&apos;s execution page or a workstream&apos;s Overview, or give a policy a <strong>tag</strong> so it applies automatically wherever the tag matches.</li>
          </ol>

          <div className="policies-wrap">
            <Lane num="01" title="Policies" sub="Groups of rules, applied together" count={catalog.policies.length}
              addLabel="+ new policy" onAdd={() => setEditor({ kind: 'policy', id: null, value: NEW_POLICY })}>
              {catalog.policies.length === 0 && <p className="policies-empty">No policies yet. Create rules, then group them into a policy.</p>}
              {catalog.policies.map((p) => (
                <PolicyCard key={p.id} policy={p} catalog={catalog} busy={busy}
                  onToggle={() => void run(() => forgeApi.updateRulePolicy(productId!, p.id, { ...p, enabled: !p.enabled }))}
                  onEdit={() => setEditor({ kind: 'policy', id: p.id, value: p })}
                  onDelete={() => void (async () => {
                    if (await confirmDelete('Delete this policy?', `${p.name} — its rules stay in the catalog; work items that had this policy lose them unless picked on their own.`)) {
                      await run(() => forgeApi.deleteRulePolicy(productId!, p.id));
                    }
                  })()} />
              ))}
            </Lane>

            <Lane num="02" title="Rules" sub="Directives; each can be in several policies or none" count={catalog.rules.length}
              addLabel="+ new rule" onAdd={() => setEditor({ kind: 'rule', id: null, value: NEW_RULE })}>
              {catalog.rules.length === 0 && <p className="policies-empty">No rules yet.</p>}
              {catalog.rules.map((r) => (
                <RuleCard key={r.id} rule={r} catalog={catalog} busy={busy}
                  onToggle={() => void run(() => forgeApi.updateCatalogRule(productId!, r.id, { title: r.title, text: r.text, enabled: !r.enabled }))}
                  onEdit={() => editRule(r)}
                  onDelete={() => void (async () => {
                    if (await confirmDelete('Delete this rule?', `${r.title} — it is removed from every policy and every work item, from their next iteration.`)) {
                      await run(() => forgeApi.deleteCatalogRule(productId!, r.id));
                    }
                  })()} />
              ))}
            </Lane>

            <Lane num="03" title="Tags" sub="Match work items by repo and text; apply policies automatically" count={catalog.tags.length}
              addLabel="+ new tag" onAdd={() => setEditor({ kind: 'tag', id: null, value: emptyTag })}>
              {catalog.tags.length === 0 && <p className="policies-empty">No tags yet. Tags are optional.</p>}
              {catalog.tags.map((t) => (
                <TagCard key={t.id} tag={t} catalog={catalog} busy={busy}
                  matches={matches?.tagId === t.id ? matches.rows : null}
                  onCheck={() => void run(async () => setMatches({ tagId: t.id, rows: await forgeApi.listRuleTagMatches(productId!, t.id) }))}
                  onEdit={() => setEditor({ kind: 'tag', id: t.id, value: t })}
                  onDelete={() => void (async () => {
                    if (await confirmDelete('Delete this tag?', `${t.name} — policies stop applying automatically where it matched; choices made by hand stay.`)) {
                      await run(() => forgeApi.deleteRuleTag(productId!, t.id), () => setMatches(null));
                    }
                  })()} />
              ))}
            </Lane>
          </div>
        </>
      )}

      {editor && catalog && (
        <Dialog
          title={`${editor.id ? 'Edit' : 'New'} ${editor.kind}`}
          error={error}
          onClose={() => { setEditor(null); setError(null); }}
        >
          {editor.kind === 'rule' && <RuleForm initial={editor.value} catalog={catalog} busy={busy} onCancel={() => setEditor(null)} onSave={save} />}
          {editor.kind === 'policy' && <PolicyForm initial={editor.value} catalog={catalog} busy={busy} onCancel={() => setEditor(null)} onSave={save} />}
          {editor.kind === 'tag' && <TagForm initial={editor.value} busy={busy} onCancel={() => setEditor(null)} onSave={save} />}
        </Dialog>
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

function Toggle({ name, on, busy, onToggle }: { name: string; on: boolean; busy?: boolean; onToggle?: () => void }) {
  if (!onToggle) return null;
  return (
    <button type="button" className="policy-toggle" data-on={on || undefined} disabled={busy}
      aria-pressed={on} aria-label={`${name}: ${on ? 'on, click to turn off' : 'off, click to turn on'}`} onClick={onToggle} />
  );
}

function Foot({ attached, detached, children }: { attached: number; detached: number; children?: ReactNode }) {
  return (
    <div className="policy-card-foot">
      <span>
        picked on <span className="num">{attached}</span> work item{attached === 1 ? '' : 's'}
        {detached > 0 && <> · removed on <span className="num">{detached}</span></>}
      </span>
      <span className="policy-card-actions">{children}</span>
    </div>
  );
}

/** A policy: its rules, how it applies (tags or by hand), and where it is picked. */
export function PolicyCard({ policy, catalog, busy, onToggle, onEdit, onDelete }: {
  policy: RulePolicy; catalog: RuleCatalog; busy?: boolean;
  onToggle?: () => void; onEdit?: () => void; onDelete?: () => void;
}) {
  const auto = policy.tagIds.length > 0;
  const usage = catalog.policyUsage?.[policy.id] ?? { attached: 0, detached: 0 };
  const rules = policy.ruleIds.map((id) => catalog.rules.find((r) => r.id === id)).filter((r): r is CatalogRule => !!r);
  return (
    <article className="policy-card" data-disabled={!policy.enabled || undefined} data-kind={auto ? 'tagged' : 'manual'}>
      <div className="policy-card-head">
        <div style={{ minWidth: 0 }}>
          <div className="policy-card-eyebrow">
            <span>Policy</span>
            <span className="sep">·</span>
            <span className={`severity ${auto ? 'info' : 'warn'}`}>{auto ? 'auto' : 'by hand'}</span>
            {!policy.enabled && <><span className="sep">·</span><span>off</span></>}
          </div>
          <h3 className="policy-card-title">{policy.name}</h3>
        </div>
        <Toggle name={policy.name} on={policy.enabled} busy={busy} onToggle={onToggle} />
      </div>
      {policy.description && <div className="policy-card-body">{policy.description}</div>}
      {rules.length === 0
        ? <div className="policy-card-body">No rules in this policy yet.</div>
        : (
          <ol className="policy-card-rules">
            {rules.map((r) => <li key={r.id}>{r.title}{!r.enabled && <span className="policy-card-off"> (off)</span>}</li>)}
          </ol>
        )}
      <div className="policy-card-trigger">
        <span className="key">applies</span>{' '}
        <span className="op">
          {auto ? `where tag ${namesOf(catalog.tags, policy.tagIds, (t) => t.name).join(' or ')} matches, or where picked` : 'where picked on a work item'}
        </span>
      </div>
      <Foot attached={usage.attached} detached={usage.detached}>
        {onEdit && <button type="button" disabled={busy} onClick={onEdit}>Edit</button>}
        {onDelete && <button type="button" className="danger" disabled={busy} onClick={onDelete}>Delete</button>}
      </Foot>
    </article>
  );
}

/** A rule: its directive, the policies it is in, and where it is picked on its own. */
export function RuleCard({ rule, catalog, busy, onToggle, onEdit, onDelete }: {
  rule: CatalogRule; catalog: RuleCatalog; busy?: boolean;
  onToggle?: () => void; onEdit?: () => void; onDelete?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const inPolicies = policiesWithRule(catalog, rule.id);
  const usage = catalog.ruleUsage?.[rule.id] ?? { attached: 0, detached: 0 };
  const long = rule.text.length > 280 || rule.text.split('\n').length > 5;
  return (
    <article className="policy-card" data-disabled={!rule.enabled || undefined} data-kind="rule">
      <div className="policy-card-head">
        <div style={{ minWidth: 0 }}>
          <div className="policy-card-eyebrow">
            <span>Rule</span>
            <span className="sep">·</span>
            <span>{inPolicies.length === 0 ? 'in no policy' : `in ${inPolicies.map((p) => p.name).join(', ')}`}</span>
            {!rule.enabled && <><span className="sep">·</span><span>off</span></>}
          </div>
          <h3 className="policy-card-title">{rule.title}</h3>
        </div>
        <Toggle name={rule.title} on={rule.enabled} busy={busy} onToggle={onToggle} />
      </div>
      <div className={`policy-card-body policy-card-body--rule${open || !long ? '' : ' is-clamped'}`}>{rule.text}</div>
      {long && (
        <button type="button" className="policy-card-more" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Show less' : 'Show all'}
        </button>
      )}
      <Foot attached={usage.attached} detached={usage.detached}>
        {onEdit && <button type="button" disabled={busy} onClick={onEdit}>Edit</button>}
        {onDelete && <button type="button" className="danger" disabled={busy} onClick={onDelete}>Delete</button>}
      </Foot>
    </article>
  );
}

/** A tag: what it matches, the policies it applies, and (once checked) the workstreams it matches. */
export function TagCard({ tag, catalog, busy, matches, onCheck, onEdit, onDelete }: {
  tag: RuleTag; catalog: RuleCatalog; busy?: boolean; matches?: RuleTagMatch[] | null;
  onCheck?: () => void; onEdit?: () => void; onDelete?: () => void;
}) {
  const applies = policiesWithTag(catalog, tag.id);
  const hits = matches?.filter((m) => m.applied) ?? [];
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
        <span className="key">matches</span> <span className="op">{tagMatchers(tag)}</span>
      </div>
      <div className="policy-card-foot">
        <span>{applies.length === 0 ? 'applies no policy yet' : `applies ${applies.map((p) => p.name).join(', ')}`}</span>
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