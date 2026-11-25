// Phase 108 — pure helpers for the product rule catalog UI: parsing the
// editor's text fields into lists and describing a tag's scope and matchers.

import type { AppliedTag, CatalogRule, RuleChoice, RuleTag, RuleTagInput } from '@/api/forgeApi';

/** One entry per non-empty line (phrases and patterns may contain commas). */
export function lines(text: string): string[] {
  return text.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0);
}

/** One entry per comma- or line-separated item (repo ids). */
export function items(text: string): string[] {
  return text.split(/[,\r\n]/).map((s) => s.trim()).filter((s) => s.length > 0);
}

/** "agent-plugin · impl" / "every repo · every workstream". */
export function tagScope(tag: Pick<RuleTagInput, 'repoIds' | 'categories'>): string {
  const repos = tag.repoIds.length === 0 ? 'every repo' : tag.repoIds.join(', ');
  const cats = tag.categories.length === 0 ? 'every workstream' : tag.categories.join(', ');
  return `${repos} · ${cats}`;
}

/** What makes the tag apply, in words. */
export function tagMatchers(tag: Pick<RuleTagInput, 'phrases' | 'patterns'>): string {
  const parts: string[] = [];
  if (tag.phrases.length > 0) parts.push(`${tag.phrases.length} phrase${tag.phrases.length === 1 ? '' : 's'}`);
  if (tag.patterns.length > 0) parts.push(`${tag.patterns.length} pattern${tag.patterns.length === 1 ? '' : 's'}`);
  return parts.length === 0 ? 'applies to every workstream in scope' : `stage text has any of ${parts.join(' or ')}`;
}

export function rulesUsingTag(rules: CatalogRule[], tagId: string): CatalogRule[] {
  return rules.filter((r) => r.tagIds.includes(tagId));
}

export function tagNames(tags: RuleTag[], ids: string[]): string[] {
  return ids.map((id) => tags.find((t) => t.id === id)?.name ?? id);
}

/** The operator's current choice on a workstream tag: matchers decide, forced on, or forced off. */
export type TagChoice = 'auto' | 'on' | 'off';

export function tagChoice(tag: AppliedTag): TagChoice {
  if (tag.source === 'operator-added') return 'on';
  if (tag.source === 'operator-removed') return 'off';
  return 'auto';
}

/**
 * The request that makes a rule's checkbox read `checked` on a work item:
 * attach (`on`), detach (`off`), or drop the override (`clear`) when the
 * rule's tags would already give that answer.
 */
export function ruleAction(choice: RuleChoice, checked: boolean): 'on' | 'off' | 'clear' {
  const byTags = choice.via.length > 0;
  if (checked) return byTags ? 'clear' : 'on';
  return byTags ? 'off' : 'clear';
}

/** Why a rule does or does not reach the work item, in words. */
export function ruleStatus(choice: RuleChoice): string {
  const tags = choice.via.join(', ');
  switch (choice.source) {
    case 'tag': return `via tag ${tags}`;
    case 'attached': return 'attached to this work item';
    case 'detached': return `detached from this work item${tags ? ` (tag ${tags} applies)` : ''}`;
    case 'disabled': return 'disabled in the catalog';
    default: return 'not attached';
  }
}

export const emptyTag: RuleTagInput = { name: '', description: '', repoIds: [], categories: [], phrases: [], patterns: [] };