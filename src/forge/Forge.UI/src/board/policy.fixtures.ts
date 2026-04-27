// Shared parity fixtures used by both `policy.spec.ts` (TS, Vitest) and
// `DragPolicyParityTests.cs` (C#, xUnit). Each scenario declares the input
// and the expected `checkDragPolicy` result. Both implementations MUST
// produce the same result for every row — the C# test loads this file as
// JSON via the build step.

import type { Card, ColumnId } from '@/types';

export interface PolicyScenario {
  /** Human-readable name shown in the test runner output. */
  name: string;
  fromCol: ColumnId;
  toCol: ColumnId;
  card: Card;
  expected: {
    allow: boolean;
    confirm?: boolean;
    title?: string;
    text?: string;
    warn?: string;
    toast?: string;
  };
}

const baseCard: Card = {
  id: '#100',
  epic: 'EPIC-024',
  col: 'backlog',
  type: 'story',
  title: 'parity sample',
  tags: [],
  pts: 3,
  priority: 'med',
  agent: null,
  cost: 0,
};

const gateCard: Card = {
  ...baseCard,
  id: 'PR #240',
  col: 'review',
  humanGate: true,
  gateReason: '/security/ path requires human approval',
};

export const policyScenarios: PolicyScenario[] = [
  {
    name: 'no-op (same column)',
    fromCol: 'design', toCol: 'design',
    card: { ...baseCard, col: 'design' },
    expected: { allow: true },
  },
  {
    name: 'forward by 1 — design → build',
    fromCol: 'design', toCol: 'build',
    card: { ...baseCard, col: 'design' },
    expected: { allow: true, toast: 'Moved to build' },
  },
  {
    name: 'forward by 1 — review → done with humanGate triggers confirm',
    fromCol: 'review', toCol: 'done',
    card: gateCard,
    expected: {
      allow: true,
      confirm: true,
      title: 'Approve human-gated change?',
      text: '<strong>PR #240</strong> has been escalated for your review because: /security/ path requires human approval. Approving will merge to main.',
    },
  },
  {
    name: 'backward — build → backlog',
    fromCol: 'build', toCol: 'backlog',
    card: { ...baseCard, col: 'build' },
    expected: { allow: true, toast: 'Card moved back · agent reassigned' },
  },
  {
    name: 'backward from done — re-open warning',
    fromCol: 'done', toCol: 'review',
    card: { ...baseCard, col: 'done' },
    expected: {
      allow: true,
      warn: 'Re-open shipped item?',
      toast: 'Item re-opened — agents will re-investigate',
    },
  },
  {
    name: 'skip-phase by 2 — backlog → build',
    fromCol: 'backlog', toCol: 'build',
    card: { ...baseCard, col: 'backlog' },
    expected: {
      allow: true,
      confirm: true,
      title: 'Skip phase?',
      text: "You're moving <strong>#100</strong> directly from <strong>backlog</strong> to <strong>build</strong>. This bypasses the 1 intermediate phase.",
    },
  },
  {
    name: 'skip-phase by 3 — backlog → review (multiple skipped)',
    fromCol: 'backlog', toCol: 'review',
    card: { ...baseCard, col: 'backlog' },
    expected: {
      allow: true,
      confirm: true,
      title: 'Skip phase?',
      text: "You're moving <strong>#100</strong> directly from <strong>backlog</strong> to <strong>review</strong>. This bypasses the 2 intermediate phases.",
    },
  },
  {
    name: 'bypass review — design → done',
    fromCol: 'design', toCol: 'done',
    card: { ...baseCard, col: 'design' },
    expected: {
      allow: true,
      confirm: true,
      title: 'Bypass review?',
      text: "You're marking <strong>#100</strong> as done without going through review. Code will not be reviewed by an agent and CI gates are skipped.",
    },
  },
];
