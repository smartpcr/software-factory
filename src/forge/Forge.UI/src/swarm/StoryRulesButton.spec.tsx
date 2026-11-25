// Story-wide standing rules: pins with no work item apply to the whole story
// and are listed first; scoped pins are listed apart with a short scope label.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { scopeLabel, splitPins, StoryRulesList, type StoryPin } from './StoryRulesButton';

const pin = (id: string, createdAt: string, workItemId: string | null, text = `rule ${id}`): StoryPin =>
  ({ id, text, workItemId, createdAt, createdBy: 'user-1' });

describe('splitPins', () => {
  it('separates story-wide pins from scoped ones, oldest first', () => {
    const { story, scoped } = splitPins([
      pin('b', '2026-11-25T10:00:00Z', null),
      pin('s', '2026-11-25T09:00:00Z', 'plan-doc:spawn-x:tech-spec.md'),
      pin('a', '2026-11-25T08:00:00Z', null),
    ]);
    expect(story.map((p) => p.id)).toEqual(['a', 'b']);
    expect(scoped.map((p) => p.id)).toEqual(['s']);
  });
});

describe('scopeLabel', () => {
  it('names a plan doc by its file', () => {
    expect(scopeLabel('plan-doc:spawn-harness-DSH-M2:tech-spec.md')).toBe('tech-spec.md');
  });

  it('names a workstream by its stage slug', () => {
    expect(scopeLabel('ws-harness-dsh-m2-phase-dh-s-12-x-stage-dh-s-12-scenarios-and-built-record'))
      .toBe('dh-s-12-scenarios-and-built-record');
  });

  it('keeps any other id as it is', () => {
    expect(scopeLabel('spawn-harness-DSH-M2')).toBe('spawn-harness-DSH-M2');
  });
});

describe('StoryRulesList', () => {
  it('says when the story has no story-wide rule', () => {
    const out = renderToStaticMarkup(<StoryRulesList pins={[]} />);
    expect(out).toContain('Whole story · 0');
    expect(out).toContain('No story-wide rules yet.');
    expect(out).not.toContain('One document or workstream');
  });

  it('lists story-wide rules, then scoped ones with their scope, each removable', () => {
    const out = renderToStaticMarkup(
      <StoryRulesList
        pins={[
          pin('a', '2026-11-25T08:00:00Z', null, 'Never run the full gate in an iteration.'),
          pin('s', '2026-11-25T09:00:00Z', 'plan-doc:spawn-x:tech-spec.md', 'HC-3 is authoritative.'),
        ]}
        onRemove={() => undefined}
      />,
    );
    expect(out).toContain('Whole story · 1');
    expect(out).toContain('Never run the full gate in an iteration.');
    expect(out).toContain('One document or workstream · 1');
    expect(out).toContain('tech-spec.md');
    expect(out.indexOf('Never run the full gate')).toBeLessThan(out.indexOf('HC-3 is authoritative.'));
    expect(out.match(/Remove/g)).toHaveLength(2);
  });

  it('offers no Remove button when the list is read-only', () => {
    const out = renderToStaticMarkup(<StoryRulesList pins={[pin('a', '2026-11-25T08:00:00Z', null)]} />);
    expect(out).not.toContain('Remove');
  });
});