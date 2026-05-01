import type { PrComment } from '@/types';

// Phase 28.2 — derive a single high-level approval verdict from the
// stream of vote events ADO syncs as system comments. We keep this in
// the UI for now (computing on the wire from `pr.review` would require
// a server change to aggregate votes into ReviewVerdict). The detail
// panel surfaces the derived verdict; the PR card falls back to the
// already-aggregated `pr.review.verdict` because cards can't afford to
// fetch comments.

export type ApprovalVerdict =
  | 'approved'
  | 'rejected'
  | 'changes'
  | 'wait'
  | 'pending';

interface VoteEvent {
  authorId: string;
  vote: number;
  at: string;
}

/**
 * Pull each reviewer's most-recent vote out of the system comment
 * stream. ADO posts one row per vote change (+10 approve, +5 approve
 * with suggestions, 0 reset, -5 wait, -10 reject), so we keep only
 * the latest per reviewer to avoid double-counting.
 */
function latestVotesByReviewer(comments: readonly PrComment[]): Map<string, VoteEvent> {
  const latest = new Map<string, VoteEvent>();
  for (const c of comments) {
    if (c.authorKind !== 'system') continue;
    const body = c.body ?? '';
    const m = /^(.+?)\s+voted\s+(-?\d+)/i.exec(body);
    if (!m) continue;
    const reviewer = m[1].trim();
    const vote = Number.parseInt(m[2], 10);
    if (!Number.isFinite(vote)) continue;
    const prev = latest.get(reviewer);
    if (!prev || prev.at.localeCompare(c.createdAt) < 0) {
      latest.set(reviewer, { authorId: reviewer, vote, at: c.createdAt });
    }
  }
  return latest;
}

/**
 * True for service-account voters whose vote shouldn't count as a
 * human approval. ADO's "One Build Service" auto-approves with +10
 * the moment a required pipeline goes green, but the PR is still
 * "Active — 0 of N required reviewers approve" in the web UI until
 * a human signs off. Surface that distinction by ignoring these
 * voters when deriving an approval verdict.
 *
 * Also covers the policy-bot voters ADO sometimes registers
 * (Ownership Enforcer, etc.) which behave the same way.
 */
function isAutomatedVoter(name: string): boolean {
  const lc = name.toLowerCase();
  if (lc.includes('build service')) return true;
  if (lc.includes('pipeline')) return true;
  if (lc.includes('ownership enforcer')) return true;
  if (lc.includes('policy enforcer')) return true;
  if (lc.includes('automation')) return true;
  if (/\bbot\b/.test(lc)) return true;
  return false;
}

/**
 * Resolve the current PR-level approval verdict from `comments`.
 *
 *   – any HUMAN −10 vote          → rejected
 *   – any HUMAN −5 vote           → wait (changes still requested)
 *   – any HUMAN < 0 vote          → changes
 *   – ≥ 1 HUMAN reviewer ≥ +5     → approved
 *   – otherwise                   → pending
 *
 * Build-service / policy-bot votes are ignored — see
 * {@link isAutomatedVoter}. Their negative votes still bubble up
 * because a build failure is real signal even when the PR has no
 * human review yet.
 */
export function deriveVerdict(comments: readonly PrComment[]): ApprovalVerdict {
  const votes = latestVotesByReviewer(comments);
  if (votes.size === 0) return 'pending';

  let hasHumanReject = false;
  let hasHumanWait = false;
  let hasHumanChanges = false;
  let hasHumanApprove = false;
  let hasBotReject = false;
  let hasBotChanges = false;

  for (const v of votes.values()) {
    const automated = isAutomatedVoter(v.authorId);
    if (v.vote <= -10) {
      if (automated) hasBotReject = true; else hasHumanReject = true;
    } else if (v.vote === -5) {
      if (!automated) hasHumanWait = true;
    } else if (v.vote < 0) {
      if (automated) hasBotChanges = true; else hasHumanChanges = true;
    } else if (v.vote >= 5) {
      if (!automated) hasHumanApprove = true;
    }
  }

  // Hard fails (rejected / changes) win regardless of source — a
  // failed required build, like a human −10, blocks the PR.
  if (hasHumanReject || hasBotReject)  return 'rejected';
  if (hasHumanChanges || hasBotChanges) return 'changes';
  if (hasHumanWait)                     return 'wait';
  if (hasHumanApprove)                  return 'approved';
  return 'pending';
}

const LABEL: Record<ApprovalVerdict, string> = {
  approved: 'Approved',
  rejected: 'Rejected',
  changes:  'Changes',
  wait:     'Waiting',
  pending:  'Pending',
};

const CLS: Record<ApprovalVerdict, string> = {
  approved: 'pr-verdict-approved',
  rejected: 'pr-verdict-rejected',
  changes:  'pr-verdict-changes',
  wait:     'pr-verdict-wait',
  pending:  'pr-verdict-pending',
};

export function verdictLabel(v: ApprovalVerdict): string { return LABEL[v]; }
export function verdictClass(v: ApprovalVerdict): string { return CLS[v]; }
