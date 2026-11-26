// Phase 109 — the Policies page detail panel: what opens on the right when a
// policy, rule or tag is clicked in the tree (or created). Every change saves
// at once except the text fields, which save with their Save button.
//
//   Policy: name and purpose; its rules (add, remove, reorder, new); where it
//           applies — the whole product, whole stories, tags, and single
//           workstreams or work items (picked or removed by hand).
//   Rule:   title and directive; the policies that contain it; its own tags.
//   Tag:    what it matches; the policies and rules it applies; its matches.

import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import {
  forgeApi,
  type CatalogRule,
  type CatalogRuleInput,
  type PolicyAssignments,
  type RuleCatalog,
  type RulePolicy,
  type RulePolicyInput,
  type RuleTag,
  type RuleTagMatch,
  type StoryTarget,
  type WorkItemTarget,
} from '@/api/forgeApi';
import { emptyTag, policiesWithRule, policiesWithTag, rulesWithTag, tagMatchers, tagScope } from '@/swarm/ruleCatalogText';
import { TagForm } from './policyEditors';
import type { Selection } from './policyTree';

export interface PanelContext {
  productId: string;
  productName: string;
  catalog: RuleCatalog;
  stories: StoryTarget[];
  busy: boolean;
  /** Runs a change, reloads the product's catalog, and reports whether it worked. */
  run: <T>(action: () => Promise<T>) => Promise<T | undefined>;
  select: (sel: Selection | null) => void;
  confirm: (title: string, body: string) => Promise<boolean>;
}

// ── Small pieces ─────────────────────────────────────────────────────────

export function Toggle({ name, on, busy, onToggle }: { name: string; on: boolean; busy?: boolean; onToggle: () => void }) {
  return (
    <button type="button" className="policy-toggle" data-on={on || undefined} disabled={busy}
      aria-pressed={on} aria-label={`${name}: ${on ? 'on, click to turn off' : 'off, click to turn on'}`} onClick={onToggle} />
  );
}

function Section({ title, hint, children }: { title: string; hint?: ReactNode; children: ReactNode }) {
  const id = useId();
  return (
    <section className="px-section" aria-labelledby={id}>
      <h3 id={id} className="px-section-title">{title}</h3>
      {hint && <p className="rx-hint px-section-hint">{hint}</p>}
      {children}
    </section>
  );
}

/** A removable value; clicking the name opens it when `onOpen` is given. */
function Chip({ label, title, onOpen, onRemove, busy }: {
  label: string; title?: string; onOpen?: () => void; onRemove?: () => void; busy?: boolean;
}) {
  return (
    <span className="px-chip" title={title}>
      {onOpen ? <button type="button" className="px-chip-name" onClick={onOpen}>{label}</button> : <span className="px-chip-name">{label}</span>}
      {onRemove && (
        <button type="button" className="px-chip-x" disabled={busy} aria-label={`Remove ${label}`} onClick={onRemove}>✕</button>
      )}
    </span>
  );
}

/** A native select that adds the chosen value, then resets. */
function AddPicker({ label, options, busy, onAdd }: {
  label: string; options: Array<{ value: string; label: string }>; busy?: boolean; onAdd: (value: string) => void;
}) {
  if (options.length === 0) return null;
  return (
    <select className="rx-input px-add" aria-label={label} value="" disabled={busy}
      onChange={(e) => { if (e.target.value) onAdd(e.target.value); }}>
      <option value="">{label}</option>
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}

function PanelHead({ kind, ctx, title, on, onToggle, onDelete, deleteLabel }: {
  kind: string; ctx: PanelContext; title: string; on?: boolean; onToggle?: () => void; onDelete?: () => void; deleteLabel?: string;
}) {
  return (
    <header className="px-head">
      <div className="px-head-main">
        <div className="px-eyebrow">
          <span>{kind}</span>
          <span aria-hidden="true">·</span>
          <span>{ctx.productName}</span>
          {on === false && <><span aria-hidden="true">·</span><span className="px-off">off</span></>}
        </div>
        <h2 className="px-title">{title}</h2>
      </div>
      <div className="px-head-actions">
        {onToggle && on !== undefined && <Toggle name={title} on={on} busy={ctx.busy} onToggle={onToggle} />}
        {onDelete && <button type="button" className="btn btn-secondary px-danger" disabled={ctx.busy} onClick={onDelete}>{deleteLabel ?? 'Delete'}</button>}
        <button type="button" className="rx-close" aria-label="Close the panel" onClick={() => ctx.select(null)}>✕</button>
      </div>
    </header>
  );
}

function SaveBar({ dirty, ready, busy, saveLabel, onSave, onRevert }: {
  dirty: boolean; ready: boolean; busy: boolean; saveLabel: string; onSave: () => void; onRevert?: () => void;
}) {
  return (
    <div className="rx-actions">
      {onRevert && <button type="button" className="btn btn-secondary" disabled={!dirty || busy} onClick={onRevert}>Revert</button>}
      <button type="button" className="btn btn-primary" disabled={!dirty || !ready || busy} onClick={onSave}>
        {busy ? 'Saving…' : saveLabel}
      </button>
    </div>
  );
}

const policyInput = (p: RulePolicy, patch: Partial<RulePolicyInput> = {}): RulePolicyInput => ({
  name: p.name, description: p.description, ruleIds: p.ruleIds, tagIds: p.tagIds, enabled: p.enabled, ...patch,
});

const ruleInput = (r: CatalogRule, catalog: RuleCatalog, patch: Partial<CatalogRuleInput> = {}): CatalogRuleInput => ({
  title: r.title,
  text: r.text,
  enabled: r.enabled,
  policyIds: policiesWithRule(catalog, r.id).map((p) => p.id),
  tagIds: r.tagIds,
  ...patch,
});

// ── Policy ───────────────────────────────────────────────────────────────

export function PolicyPanel({ ctx, policy }: { ctx: PanelContext; policy: RulePolicy | null }) {
  const id = useId();
  const { catalog, productId } = ctx;
  const [name, setName] = useState(policy?.name ?? '');
  const [description, setDescription] = useState(policy?.description ?? '');
  const [assignments, setAssignments] = useState<PolicyAssignments | null>(null);
  const policyId = policy?.id;

  const loadAssignments = useCallback(async () => {
    if (!policyId) return;
    try { setAssignments(await forgeApi.getPolicyAssignments(productId, policyId)); } catch { setAssignments(null); }
  }, [productId, policyId]);
  useEffect(() => { void loadAssignments(); }, [loadAssignments]);

  if (!policy) {
    return (
      <div className="px-panel">
        <PanelHead kind="New policy" ctx={ctx} title="New policy" />
        <div className="px-body">
          <NameFields id={id} name={name} description={description} setName={setName} setDescription={setDescription} />
          <p className="rx-hint">After creating it, add its rules and choose where it applies.</p>
          <SaveBar dirty={!!name.trim()} ready={!!name.trim()} busy={ctx.busy} saveLabel="Create policy"
            onSave={() => void ctx.run(() => forgeApi.addRulePolicy(productId, {
              name: name.trim(), description: description.trim(), ruleIds: [], tagIds: [], enabled: true,
            })).then((p) => { if (p) ctx.select({ productId, kind: 'policy', id: p.id }); })} />
        </div>
      </div>
    );
  }

  const update = (patch: Partial<RulePolicyInput>) => ctx.run(() => forgeApi.updateRulePolicy(productId, policy.id, policyInput(policy, patch)));
  const afterScope = (a: PolicyAssignments | undefined) => { if (a) setAssignments(a); };
  const rules = policy.ruleIds.map((rid) => catalog.rules.find((r) => r.id === rid)).filter((r): r is CatalogRule => !!r);
  const scopes = assignments?.scopes ?? catalog.policyScopes[policy.id] ?? [];
  const wholeProduct = scopes.some((s) => s.kind === 'product');
  const storyScopes = scopes.filter((s) => s.kind === 'story');
  const dirty = name !== policy.name || description !== policy.description;

  const move = (from: number, to: number) => {
    const ids = [...policy.ruleIds];
    const [x] = ids.splice(from, 1);
    ids.splice(to, 0, x);
    void update({ ruleIds: ids });
  };

  return (
    <div className="px-panel">
      <PanelHead kind="Policy" ctx={ctx} title={policy.name} on={policy.enabled}
        onToggle={() => void update({ enabled: !policy.enabled })}
        onDelete={() => void (async () => {
          if (await ctx.confirm('Delete this policy?', `${policy.name}: its rules stay in the catalog; work items that had this policy lose them unless picked on their own.`)) {
            if (await ctx.run(() => forgeApi.deleteRulePolicy(productId, policy.id).then(() => true))) ctx.select(null);
          }
        })()} />
      <div className="px-body">
        <Section title="Details">
          <NameFields id={id} name={name} description={description} setName={setName} setDescription={setDescription} />
          <SaveBar dirty={dirty} ready={!!name.trim()} busy={ctx.busy} saveLabel="Save details"
            onRevert={() => { setName(policy.name); setDescription(policy.description); }}
            onSave={() => void update({ name: name.trim(), description: description.trim() })} />
        </Section>

        <Section title={`Rules (${rules.length})`} hint="Every enabled rule here goes into the prompt, in this order, wherever the policy applies.">
          {rules.length === 0 ? <p className="rx-empty">No rules in this policy yet.</p> : (
            <ol className="px-rules">
              {rules.map((r, i) => (
                <li key={r.id} className="px-rule-row">
                  <span className="px-rule-num">{i + 1}</span>
                  <button type="button" className="px-link" onClick={() => ctx.select({ productId, kind: 'rule', id: r.id })}>{r.title}</button>
                  {!r.enabled && <span className="px-off">off</span>}
                  <span className="px-row-actions">
                    <button type="button" className="rx-small-btn" disabled={ctx.busy || i === 0} aria-label={`Move ${r.title} up`} onClick={() => move(i, i - 1)}>↑</button>
                    <button type="button" className="rx-small-btn" disabled={ctx.busy || i === rules.length - 1} aria-label={`Move ${r.title} down`} onClick={() => move(i, i + 1)}>↓</button>
                    <button type="button" className="rx-small-btn" disabled={ctx.busy} aria-label={`Remove ${r.title} from this policy`}
                      onClick={() => void update({ ruleIds: policy.ruleIds.filter((x) => x !== r.id) })}>Remove</button>
                  </span>
                </li>
              ))}
            </ol>
          )}
          <div className="px-add-row">
            <AddPicker label="Add a rule…" busy={ctx.busy}
              options={catalog.rules.filter((r) => !policy.ruleIds.includes(r.id)).map((r) => ({ value: r.id, label: r.title }))}
              onAdd={(rid) => void update({ ruleIds: [...policy.ruleIds, rid] })} />
            <button type="button" className="btn btn-secondary" onClick={() => ctx.select({ productId, kind: 'rule', id: null, policyId: policy.id })}>
              New rule in this policy
            </button>
          </div>
        </Section>

        <Section title="Applies to" hint="Anything here brings the policy automatically. A choice made on one work item still wins.">
          <label className="rx-check px-scope">
            <input type="checkbox" checked={wholeProduct} disabled={ctx.busy}
              onChange={(e) => void ctx.run(() => (e.target.checked
                ? forgeApi.setPolicyScope(productId, policy.id, 'product', productId)
                : forgeApi.clearPolicyScope(productId, policy.id, 'product', productId))).then(afterScope)} />
            <span><strong>The whole product</strong>: every work item and workstream in {ctx.productName}</span>
          </label>

          <h4 className="px-sub">Stories</h4>
          <p className="rx-hint">Every workstream of these stories.</p>
          <div className="px-chips">
            {storyScopes.length === 0 && <span className="rx-empty">None.</span>}
            {storyScopes.map((s) => (
              <Chip key={s.scopeId} label={s.label} busy={ctx.busy}
                onRemove={() => void ctx.run(() => forgeApi.clearPolicyScope(productId, policy.id, 'story', s.scopeId)).then(afterScope)} />
            ))}
          </div>
          <AddPicker label="Add a story…" busy={ctx.busy}
            options={ctx.stories.filter((s) => !storyScopes.some((x) => x.scopeId === s.spawnId))
              .map((s) => ({ value: s.spawnId, label: `${s.storyId} — ${s.title} (${s.workstreams} workstreams)` }))}
            onAdd={(spawnId) => void ctx.run(() => forgeApi.setPolicyScope(productId, policy.id, 'story', spawnId)).then(afterScope)} />

          <h4 className="px-sub">Tags</h4>
          <p className="rx-hint">Wherever one of these tags matches a work item&apos;s repo and text.</p>
          <div className="px-chips">
            {policy.tagIds.length === 0 && <span className="rx-empty">None.</span>}
            {policy.tagIds.map((tid) => {
              const t = catalog.tags.find((x) => x.id === tid);
              return (
                <Chip key={tid} label={t?.name ?? tid} title={t ? tagMatchers(t) : undefined} busy={ctx.busy}
                  onOpen={t ? () => ctx.select({ productId, kind: 'tag', id: tid }) : undefined}
                  onRemove={() => void update({ tagIds: policy.tagIds.filter((x) => x !== tid) })} />
              );
            })}
          </div>
          <AddPicker label="Add a tag…" busy={ctx.busy}
            options={catalog.tags.filter((t) => !policy.tagIds.includes(t.id)).map((t) => ({ value: t.id, label: `${t.name} (${tagScope(t)})` }))}
            onAdd={(tid) => void update({ tagIds: [...policy.tagIds, tid] })} />

          <h4 className="px-sub">Single workstreams and work items</h4>
          <WorkItemPicks ctx={ctx} policy={policy} assignments={assignments} onChanged={loadAssignments} />
        </Section>

        <Meta created={policy.createdAt} by={policy.createdBy} updated={policy.updatedAt} />
      </div>
    </div>
  );
}

function NameFields({ id, name, description, setName, setDescription }: {
  id: string; name: string; description: string; setName: (v: string) => void; setDescription: (v: string) => void;
}) {
  return (
    <>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-name`}>Name</label>
        <input id={`${id}-name`} className="rx-input" value={name} maxLength={80}
          placeholder="e.g. Full harness gate" onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-desc`}>What it is for</label>
        <textarea id={`${id}-desc`} className="rx-textarea px-short" value={description}
          placeholder="e.g. Work that touches the harness gate or needs lane runs" onChange={(e) => setDescription(e.target.value)} />
      </div>
    </>
  );
}

/** The work items a policy is picked on or removed from by hand, and a search to pick it on more. */
function WorkItemPicks({ ctx, policy, assignments, onChanged }: {
  ctx: PanelContext; policy: RulePolicy; assignments: PolicyAssignments | null; onChanged: () => Promise<void>;
}) {
  const id = useId();
  const { productId } = ctx;
  const [q, setQ] = useState('');
  const [found, setFound] = useState<WorkItemTarget[] | null>(null);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    window.clearTimeout(timer.current);
    if (!q.trim()) { setFound(null); return; }
    timer.current = window.setTimeout(() => {
      void forgeApi.listRuleTargets(productId, q).then((t) => setFound(t.workItems)).catch(() => setFound([]));
    }, 250);
    return () => window.clearTimeout(timer.current);
  }, [q, productId]);

  const choose = (workItemId: string, on: boolean | null) => void ctx.run(() => (on === null
    ? forgeApi.clearWorkItemChoice(productId, workItemId, 'policies', policy.id)
    : forgeApi.setWorkItemChoice(productId, workItemId, 'policies', policy.id, on))).then(() => onChanged());

  const picks = assignments?.workItems ?? [];
  const pickedIds = new Set(picks.map((p) => p.id));
  return (
    <>
      <p className="rx-hint">Picked here, the policy applies to that one item; removed here, it stays out even where a story, the product or a tag would bring it.</p>
      {picks.length === 0 ? <p className="rx-empty">None.</p> : (
        <ul className="px-items">
          {picks.map((w) => (
            <li key={w.id} className="px-item">
              <span className={`px-state ${w.on ? 'is-on' : 'is-off'}`}>{w.on ? 'picked' : 'removed'}</span>
              <span className="px-item-name" title={w.id}>{w.title}</span>
              <span className="rx-hint">{w.kind}{w.storyId ? ` · ${w.storyId}` : ''}</span>
              <span className="px-row-actions">
                <button type="button" className="rx-small-btn" disabled={ctx.busy} onClick={() => choose(w.id, !w.on)}>
                  {w.on ? 'Remove here' : 'Pick here'}
                </button>
                <button type="button" className="rx-small-btn" disabled={ctx.busy} aria-label={`Clear the choice on ${w.title}`} onClick={() => choose(w.id, null)}>Clear</button>
              </span>
            </li>
          ))}
        </ul>
      )}
      <div className="rx-field px-search">
        <label className="rx-label" htmlFor={`${id}-q`}>Find a workstream or work item</label>
        <input id={`${id}-q`} className="rx-input" type="search" value={q} placeholder="Title, id or story, e.g. DSH-M2 3.2"
          onChange={(e) => setQ(e.target.value)} />
      </div>
      {found && (found.length === 0 ? <p className="rx-empty">Nothing found.</p> : (
        <ul className="px-items" aria-label="Search results">
          {found.slice(0, 15).map((w) => (
            <li key={w.id} className="px-item">
              <span className="px-item-name" title={w.id}>{w.title}</span>
              <span className="rx-hint">{w.kind}{w.storyId ? ` · ${w.storyId}` : ''} · {w.status}</span>
              <span className="px-row-actions">
                <button type="button" className="rx-small-btn" disabled={ctx.busy || pickedIds.has(w.id)} onClick={() => choose(w.id, true)}>
                  {pickedIds.has(w.id) ? 'Listed above' : 'Pick here'}
                </button>
              </span>
            </li>
          ))}
        </ul>
      ))}
    </>
  );
}

// ── Rule ─────────────────────────────────────────────────────────────────

export function RulePanel({ ctx, rule, startPolicyId }: { ctx: PanelContext; rule: CatalogRule | null; startPolicyId?: string }) {
  const id = useId();
  const { catalog, productId } = ctx;
  const [title, setTitle] = useState(rule?.title ?? '');
  const [text, setText] = useState(rule?.text ?? '');

  if (!rule) {
    const start = catalog.policies.find((p) => p.id === startPolicyId);
    return (
      <div className="px-panel">
        <PanelHead kind="New rule" ctx={ctx} title="New rule" />
        <div className="px-body">
          <RuleFields id={id} title={title} text={text} setTitle={setTitle} setText={setText} />
          <p className="rx-hint">{start ? `It starts in the policy ${start.name}.` : 'It starts in no policy; add it to policies or give it tags after creating it.'}</p>
          <SaveBar dirty={!!title.trim() && !!text.trim()} ready={!!title.trim() && !!text.trim()} busy={ctx.busy} saveLabel="Create rule"
            onSave={() => void ctx.run(() => forgeApi.addCatalogRule(productId, {
              title: title.trim(), text: text.trim(), enabled: true, policyIds: start ? [start.id] : [], tagIds: [],
            })).then((r) => { if (r) ctx.select({ productId, kind: 'rule', id: r.id }); })} />
        </div>
      </div>
    );
  }

  const update = (patch: Partial<CatalogRuleInput>) => ctx.run(() => forgeApi.updateCatalogRule(productId, rule.id, ruleInput(rule, catalog, patch)));
  const inPolicies = policiesWithRule(catalog, rule.id);
  const usage = catalog.ruleUsage[rule.id] ?? { attached: 0, detached: 0 };
  const dirty = title !== rule.title || text !== rule.text;
  return (
    <div className="px-panel">
      <PanelHead kind="Rule" ctx={ctx} title={rule.title} on={rule.enabled}
        onToggle={() => void update({ enabled: !rule.enabled })}
        onDelete={() => void (async () => {
          if (await ctx.confirm('Delete this rule?', `${rule.title}: it is removed from every policy and every work item, from their next iteration.`)) {
            if (await ctx.run(() => forgeApi.deleteCatalogRule(productId, rule.id).then(() => true))) ctx.select(null);
          }
        })()} />
      <div className="px-body">
        <Section title="Directive">
          <RuleFields id={id} title={title} text={text} setTitle={setTitle} setText={setText} />
          <SaveBar dirty={dirty} ready={!!title.trim() && !!text.trim()} busy={ctx.busy} saveLabel="Save rule"
            onRevert={() => { setTitle(rule.title); setText(rule.text); }}
            onSave={() => void update({ title: title.trim(), text: text.trim() })} />
        </Section>

        <Section title={`In policies (${inPolicies.length})`} hint="The rule reaches every work item these policies apply to.">
          <div className="px-chips">
            {inPolicies.length === 0 && <span className="rx-empty">In no policy.</span>}
            {inPolicies.map((p) => (
              <Chip key={p.id} label={p.name} busy={ctx.busy}
                onOpen={() => ctx.select({ productId, kind: 'policy', id: p.id })}
                onRemove={() => void update({ policyIds: inPolicies.filter((x) => x.id !== p.id).map((x) => x.id) })} />
            ))}
          </div>
          <AddPicker label="Add to a policy…" busy={ctx.busy}
            options={catalog.policies.filter((p) => !inPolicies.includes(p)).map((p) => ({ value: p.id, label: p.name }))}
            onAdd={(pid) => void update({ policyIds: [...inPolicies.map((x) => x.id), pid] })} />
        </Section>

        <Section title={`Tags (${rule.tagIds.length})`} hint="The rule also applies on its own wherever one of these tags matches, even with no policy.">
          <div className="px-chips">
            {rule.tagIds.length === 0 && <span className="rx-empty">No tags.</span>}
            {rule.tagIds.map((tid) => {
              const t = catalog.tags.find((x) => x.id === tid);
              return (
                <Chip key={tid} label={t?.name ?? tid} title={t ? tagMatchers(t) : undefined} busy={ctx.busy}
                  onOpen={t ? () => ctx.select({ productId, kind: 'tag', id: tid }) : undefined}
                  onRemove={() => void update({ tagIds: rule.tagIds.filter((x) => x !== tid) })} />
              );
            })}
          </div>
          <div className="px-add-row">
            <AddPicker label="Add a tag…" busy={ctx.busy}
              options={catalog.tags.filter((t) => !rule.tagIds.includes(t.id)).map((t) => ({ value: t.id, label: `${t.name} (${tagScope(t)})` }))}
              onAdd={(tid) => void update({ tagIds: [...rule.tagIds, tid] })} />
            <button type="button" className="btn btn-secondary" onClick={() => ctx.select({ productId, kind: 'tag', id: null })}>New tag</button>
          </div>
        </Section>

        <Section title="Picked by hand">
          <p className="rx-hint">
            Picked on its own on {usage.attached} work item{usage.attached === 1 ? '' : 's'}
            {usage.detached > 0 ? `; kept out on ${usage.detached}` : ''}. Pick or remove it on a work item&apos;s execution page or a workstream&apos;s Overview.
          </p>
        </Section>

        <Meta created={rule.createdAt} by={rule.createdBy} updated={rule.updatedAt} />
      </div>
    </div>
  );
}

function RuleFields({ id, title, text, setTitle, setText }: {
  id: string; title: string; text: string; setTitle: (v: string) => void; setText: (v: string) => void;
}) {
  return (
    <>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-title`}>Title</label>
        <input id={`${id}-title`} className="rx-input" value={title} maxLength={120}
          placeholder="e.g. Never run the full gate in an iteration" onChange={(e) => setTitle(e.target.value)} />
      </div>
      <div className="rx-field">
        <label className="rx-label" htmlFor={`${id}-text`}>What the agent must do</label>
        <textarea id={`${id}-text`} className="rx-textarea px-tall" maxLength={8000} value={text}
          placeholder="Write the directive the agent follows on every iteration." onChange={(e) => setText(e.target.value)} />
      </div>
    </>
  );
}

// ── Tag ──────────────────────────────────────────────────────────────────

export function TagPanel({ ctx, tag }: { ctx: PanelContext; tag: RuleTag | null }) {
  const { catalog, productId } = ctx;
  const [matches, setMatches] = useState<RuleTagMatch[] | null>(null);
  const [formKey, setFormKey] = useState(0);

  if (!tag) {
    return (
      <div className="px-panel">
        <PanelHead kind="New tag" ctx={ctx} title="New tag" />
        <div className="px-body">
          <TagForm initial={emptyTag} busy={ctx.busy} cancelLabel="Cancel" onCancel={() => ctx.select(null)}
            onSave={(v) => void ctx.run(() => forgeApi.addRuleTag(productId, v)).then((t) => { if (t) ctx.select({ productId, kind: 'tag', id: t.id }); })} />
        </div>
      </div>
    );
  }

  const policies = policiesWithTag(catalog, tag.id);
  const rules = rulesWithTag(catalog, tag.id);
  const hits = matches?.filter((m) => m.applied) ?? [];
  return (
    <div className="px-panel">
      <PanelHead kind="Tag" ctx={ctx} title={tag.name}
        onDelete={() => void (async () => {
          if (await ctx.confirm('Delete this tag?', `${tag.name}: policies and rules stop applying automatically where it matched; choices made by hand stay.`)) {
            if (await ctx.run(() => forgeApi.deleteRuleTag(productId, tag.id).then(() => true))) ctx.select(null);
          }
        })()} />
      <div className="px-body">
        <Section title="Used by" hint="Wherever this tag matches, these policies and rules apply.">
          <div className="px-chips">
            {policies.length + rules.length === 0 && <span className="rx-empty">Nothing uses this tag yet. Add it on a policy or a rule.</span>}
            {policies.map((p) => <Chip key={p.id} label={`Policy: ${p.name}`} onOpen={() => ctx.select({ productId, kind: 'policy', id: p.id })} />)}
            {rules.map((r) => <Chip key={r.id} label={`Rule: ${r.title}`} onOpen={() => ctx.select({ productId, kind: 'rule', id: r.id })} />)}
          </div>
        </Section>

        <Section title="Matches" hint={`${tagScope(tag)}; ${tagMatchers(tag)}.`}>
          <button type="button" className="btn btn-secondary" disabled={ctx.busy}
            onClick={() => void forgeApi.listRuleTagMatches(productId, tag.id).then(setMatches).catch(() => setMatches([]))}>
            {matches ? 'Check again' : 'Check which workstreams match'}
          </button>
          {matches && (
            <div className="px-matches">
              <p className="rx-hint">Matches {hits.length} of {matches.length} workstreams in this product.</p>
              {hits.length > 0 && (
                <ul className="px-items">
                  {hits.slice(0, 50).map((m) => (
                    <li key={m.workstreamId} className="px-item" title={m.workstreamId}>
                      <span className="px-item-name">{m.name}</span>
                      <span className="rx-hint">{m.category} · {m.status} · {m.reason}</span>
                    </li>
                  ))}
                  {hits.length > 50 && <li className="rx-hint">and {hits.length - 50} more</li>}
                </ul>
              )}
            </div>
          )}
        </Section>

        <Section title="What it matches">
          <TagForm key={formKey} initial={tag} busy={ctx.busy} onCancel={() => setFormKey((k) => k + 1)}
            onSave={(v) => void ctx.run(() => forgeApi.updateRuleTag(productId, tag.id, v)).then(() => setMatches(null))} />
        </Section>

        <Meta created={tag.createdAt} by={tag.createdBy} updated={tag.updatedAt} />
      </div>
    </div>
  );
}

function Meta({ created, by, updated }: { created: string; by: string | null; updated: string }) {
  const fmt = (s: string) => { const d = new Date(s); return Number.isNaN(d.getTime()) ? s : d.toLocaleString(); };
  return <p className="rx-hint px-meta">Created {fmt(created)}{by ? ` by ${by}` : ''} · updated {fmt(updated)}</p>;
}

// ── Multi-select (filter bar) ────────────────────────────────────────────

export interface Option { value: string; label: string; hint?: string }

export function MultiSelect({ label, values, options, allLabel, onChange }: {
  label: string; values: string[]; options: Option[]; allLabel: string; onChange: (v: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const ref = useRef<HTMLDivElement>(null);
  const listId = useId();
  const chosen = new Set(values);

  useEffect(() => {
    if (!open) { setSearch(''); return undefined; }
    const onDown = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('mousedown', onDown); window.removeEventListener('keydown', onKey); };
  }, [open]);

  const needle = search.trim().toLowerCase();
  const shown = needle ? options.filter((o) => `${o.label} ${o.hint ?? ''}`.toLowerCase().includes(needle)) : options;
  const summary = chosen.size === 0
    ? allLabel
    : chosen.size === 1 ? options.find((o) => chosen.has(o.value))?.label ?? allLabel : `${chosen.size} chosen`;

  return (
    <div className="px-ms" ref={ref}>
      <button type="button" className="px-ms-trigger" aria-haspopup="true" aria-expanded={open} aria-controls={listId}
        onClick={() => setOpen((v) => !v)}>
        <span className="px-ms-label">{label}</span>
        <span className="px-ms-summary">{summary}</span>
        <span aria-hidden="true">▾</span>
      </button>
      {open && (
        <div className="px-ms-pop rx" id={listId}>
          <div className="px-ms-pop-head">
            <input className="rx-input" type="search" autoFocus value={search} aria-label={`Search ${label.toLowerCase()}`}
              placeholder="Search…" onChange={(e) => setSearch(e.target.value)} />
            <button type="button" className="rx-small-btn" disabled={chosen.size === 0} onClick={() => onChange([])}>Clear</button>
          </div>
          <div className="px-ms-list" role="group" aria-label={label}>
            {shown.length === 0 && <p className="rx-empty">No match.</p>}
            {shown.map((o) => (
              <label key={o.value} className="rx-check">
                <input type="checkbox" checked={chosen.has(o.value)}
                  onChange={() => onChange(chosen.has(o.value) ? values.filter((v) => v !== o.value) : [...values, o.value])} />
                <span>{o.label}</span>
                {o.hint && <span className="rx-hint">{o.hint}</span>}
              </label>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}