// Canonical `{phaseNum}.{stageNum}` labelling for swarm workstreams.
//
// Both the phase board (SwarmMasterPanel) and the workstream DAG / right
// pane (SwarmDetailPanel) must show the SAME numbers, matching
// implementation-plan.md's `# Phase N` / `## Stage N.M` headings, so the
// logic lives here as a single pure function with a vitest lock-in.
//
// docs/forge/e2e.md Stage 8.1 fix: a stage's trailing qa-e2e companion is
// NOT a stage of its own — it is rendered stacked beneath its impl sibling
// and labelled `{implNum}-e2e`. The old inline numbering counted qa-e2e
// rows positionally, so each one consumed a stage slot and bumped the next
// impl stage's number (operator saw `stage 1.1 → stage 1.1-e2e → stage 1.3`,
// skipping 1.2). Excluding qa-e2e rows from the positional count fixes it.

import type { StorySpawnWorkstreamSummary } from '@/api/forgeApi';
import { isQaE2e, qaE2eByImplId } from './qaE2eSelector';

const DEFAULT_PHASE = '__workstreams__';

/**
 * Map every workstream id → its `{phaseNum}.{stageNum}` label.
 *
 * - Phase order = first-seen order of `phaseAnchor` over `workstreams`
 *   (mirrors the spawn's Sort order, which mirrors the plan's `# Phase`
 *   order). Workstreams with no `phaseAnchor` fall into one shared bucket.
 * - Only IMPL workstreams take a positional `{phase}.{stage}` slot.
 * - Each trailing qa-e2e workstream inherits `{implSiblingNum}-e2e` so it
 *   never bumps the next impl stage's number. A qa-e2e row whose impl
 *   sibling can't be resolved is left out of the map (callers fall back to
 *   a `?? '…'` default).
 */
export function computeStageNumbers(
  workstreams: ReadonlyArray<StorySpawnWorkstreamSummary>,
): Map<string, string> {
  const labels = new Map<string, string>();
  const buckets = new Map<string, string[]>();
  const phaseOrder: string[] = [];

  for (const w of workstreams) {
    if (isQaE2e(w)) continue; // numbered via its impl sibling below
    const pa = w.phaseAnchor && w.phaseAnchor.length > 0 ? w.phaseAnchor : DEFAULT_PHASE;
    if (!buckets.has(pa)) {
      phaseOrder.push(pa);
      buckets.set(pa, []);
    }
    buckets.get(pa)!.push(w.id);
  }

  phaseOrder.forEach((pa, gi) => {
    buckets.get(pa)!.forEach((id, si) => {
      labels.set(id, `${gi + 1}.${si + 1}`);
    });
  });

  qaE2eByImplId(workstreams).forEach((qa, implId) => {
    const implNum = labels.get(implId);
    if (implNum) labels.set(qa.id, `${implNum}-e2e`);
  });

  return labels;
}
