// Phase 108 — pure helpers for the rule catalog UI (Policies page and the
// per-work-item checklist): parsing editor fields into lists, and describing
// tags, policies and rules in words.

import type { AppliedTag, PolicyChoice, RuleCatalog, RuleChoice, RulePolicy, RuleTagInput } from '@/api/forgeApi';

/** One entry per non-empty line (phrases and patterns may contain commas). */
export function lines(text: string): string[] {
  return text.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0);
}

/** One entry per comma- or line-separated item (repo ids). */
export function items(text: string): string[] {
  return text.split(/[,\r\n]/).map((s) => s.trim()).filter((s) => s.length > 0);
}

/** "agent-plugin · every workstream" / "every repo · qa-e2e". */
export function tagScope(tag: Pick<RuleTagInput, 'repoIds' | 'categories'>): string {
  const repos = tag.repoIds.length === 0 ? 'every repo' : tag.repoIds.join(', ');
  const cats = tag.categories.length === 0 ? 'every workstream' : tag.categories.join(', ');
  return `${repos} · ${cats}`;
}

/** What makes the tag apply, in words. */
export function tagMatchers(tag: Pick<RuleTagInput, 'phrases' | 'patterns'>): string {
  const terms = [...tag.phrases.map((p) => `"${p}"`), ...tag.patterns.map((p) => `/${p}/`)];
  return terms.length === 0 ? 'every work item in scope' : `text has ${terms.join(' or ')}`;
}

/** The policies that contain a rule, by name. */
export function policiesWithRule(catalog: RuleCatalog, ruleId: string): RulePolicy[] {
  return catalog.policies.filter((p) => p.ruleIds.includes(ruleId));
}

/** The policies a tag attaches. */
export function policiesWithTag(catalog: RuleCatalog, tagId: string): RulePolicy[] {
  return catalog.policies.filter((p) => p.tagIds.includes(tagId));
}

export function namesOf<T extends { id: string }>(all: T[], ids: string[], name: (t: T) => string): string[] {
  return ids.map((id) => { const t = all.find((x) => x.id === id); return t ? name(t) : id; });
}

/** The operator's current choice on a work item tag: matchers decide, forced on, or forced off. */
export type TagChoice = 'auto' | 'on' | 'off';

export function tagChoice(tag: AppliedTag): TagChoice {
  if (tag.source === 'operator-added') return 'on';
  if (tag.source === 'operator-removed') return 'off';
  return 'auto';
}

/**
 * The request that makes a policy's or rule's checkbox read `checked` on one
 * work item: attach (`on`), detach (`off`), or drop the operator's choice
 * (`clear`) when the automatic choice already gives that answer.
 */
export function choiceAction(choice: PolicyChoice | RuleChoice, checked: boolean): 'on' | 'off' | 'clear' {
  const automatic = choice.via.length > 0;
  if (checked) return automatic ? 'clear' : 'on';
  return automatic ? 'off' : 'clear';
}

/** Why a policy or rule does or does not reach the work item, in words. */
export function choiceStatus(choice: PolicyChoice | RuleChoice): string {
  const via = choice.via.join(', ');
  switch (choice.source) {
    case 'tag': return `added by tag ${via}`;
    case 'policy': return `in policy ${via}`;
    case 'attached': return 'added here by hand';
    case 'detached': return via ? `removed here by hand (would come from ${via})` : 'removed here by hand';
    case 'disabled': return 'disabled on the Policies page';
    default: return 'not applied';
  }
}

export const emptyTag: RuleTagInput = { name: '', description: '', repoIds: [], categories: [], phrases: [], patterns: [] };