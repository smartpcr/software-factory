// The "Q" metric on workstream and doc cards: answered / unanswered split,
// with a part that is 0 left out.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { QuestionsCell } from './SwarmMasterPanel';

const html = (total: number, unanswered: number) =>
  renderToStaticMarkup(<QuestionsCell total={total} unanswered={unanswered} scope="workstream" />);

describe('QuestionsCell', () => {
  it('renders nothing when no question was raised', () => {
    expect(html(0, 0)).toBe('');
  });

  it('shows only the answered count when nothing is open', () => {
    const out = html(1, 0);
    expect(out).toContain('✓1');
    expect(out).not.toContain('q-open');
  });

  it('shows only the open count, as a plain number, when nothing is answered', () => {
    const out = html(3, 3);
    expect(out).toContain('<span class="q-open">3</span>');
    expect(out).not.toContain('✓');
    expect(out).not.toContain('⚠');
  });

  it('shows both counts when some are answered and some open', () => {
    const out = html(5, 2);
    expect(out).toContain('✓3');
    expect(out).toContain('<span class="q-open">2</span>');
  });
});