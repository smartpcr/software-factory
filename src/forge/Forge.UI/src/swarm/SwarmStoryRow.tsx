import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ForgeApiError, forgeApi, type StorySpawn, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useToastStore } from '@/state/useToastStore';
import { StoryDeliveryStrip } from './StoryDeliveryStrip';

interface Props {
  spawn: StorySpawn;
  index: number;
  /**
   * Called when this row's spawn has been deleted server-side. The parent
   * (SwarmIndexView) is expected to refresh its spawn list immediately so
   * the row disappears without waiting for the 15 s polling tick.
   */
  onDeleted?: (storyId: string) => void;
}

const PHASES: StorySpawnPhase[] = ['plan', 'decompose', 'implement', 'review', 'qa', 'done'];

export function SwarmStoryRow({ spawn, index, onDeleted }: Props) {
  const navigate = useNavigate();
  const tick = useSpawnStore((s) => s.ticks[spawn.storyId] ?? 0);
  const [detail, setDetail] = useState<StorySpawnDetail | null>(null);
  const [deleting, setDeleting] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void forgeApi.getStorySpawn(spawn.storyId)
      .then((d) => { if (!cancelled) setDetail(d); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [spawn.storyId, tick]);

  const phaseIdx = PHASES.indexOf(spawn.phase);
  const pct = Math.round(((phaseIdx + 1) / PHASES.length) * 100);
  const ws = detail?.workstreams ?? [];
  const wsCount = ws.length;
  const blockedCount = ws.filter((w) => w.status === 'blocked').length;
  const mergingCount = ws.filter((w) => w.status === 'merging').length;
  const completeCount = ws.filter((w) => w.status === 'complete').length;

  // Score is not yet wired into the spawn contract; surface a neutral "—".
  // Once `WorkItemRow.score` (Phase 28 EX work) lands, swap in a real value.
  const score: number | null = null;

  /**
   * Two-step destructive flow:
   *   1) Always-shown danger confirm with a checklist of what will be wiped.
   *   2) When the spawn was opened against a non-`main` base branch,
   *      a second danger confirm asking whether to also delete the base.
   *
   * Both prompts go through the global ConfirmDialog (terminal aesthetic,
   * Esc-cancel, Enter-confirm) instead of `window.confirm` so the operator
   * sees the explicit count of side effects before clicking through. The
   * row's onClick navigates into the detail view; the button uses
   * stopPropagation so a delete click never accidentally opens the spawn.
   *
   * 409 (shared base branch) recovery: re-prompt with deleteBaseBranch=false
   * and surface the server's detail string in a toast so the operator knows
   * which other spawns share the base.
   */
  async function handleDelete(e: React.MouseEvent) {
    e.stopPropagation();
    if (!detail || deleting) return;

    const ask = useConfirmStore.getState().ask;
    const toast = useToastStore.getState().show;

    const openPrCount = ws.filter((w) => w.status === 'review' || w.status === 'merging').length;
    const stepDefPending = ws.filter((w) => w.stepDefPrId && !w.stepDefMerged).length;
    const checklist = [
      `${wsCount} workstream${wsCount === 1 ? '' : 's'} (deliverables, transcripts, queue rows)`,
      `${openPrCount + (detail.qaPrMerged ? 0 : (spawn.qaPrId ? 1 : 0))} pending PR${openPrCount === 1 ? '' : 's'} closed upstream`,
      stepDefPending > 0 ? `${stepDefPending} step-definition PR${stepDefPending === 1 ? '' : 's'} closed` : null,
      `Plan PR + all worktrees + local & remote branches removed`,
      `On-disk workspaces and attachments wiped`,
    ].filter((x): x is string => !!x);

    const ok = await ask({
      title: `DELETE STORY '${spawn.storyTitle}'?`,
      body:
        `This wipes the story spawn and every artifact it produced. Pending PRs are abandoned upstream first; merged PR rows are kept upstream but removed from Forge's local DB. This cannot be undone.`,
      checklist,
      tone: 'danger',
      confirmLabel: 'Delete spawn',
      cancelLabel: 'Keep',
    });
    if (!ok) return;

    let deleteBaseBranch = false;
    if (spawn.baseBranch && spawn.baseBranch !== 'main') {
      const okBase = await ask({
        title: `Also delete base branch '${spawn.baseBranch}'?`,
        body:
          `This spawn was opened against a non-main base branch. The base branch carries any commits other spawns merged into it. Delete it only when no other spawn (current or future) needs that branch.\n\nIf the base branch is shared with another active spawn, the server will refuse and the spawn will still be deleted without touching the base.`,
        tone: 'danger',
        confirmLabel: `Yes, delete ${spawn.baseBranch}`,
        cancelLabel: 'Keep base branch',
      });
      deleteBaseBranch = okBase;
    }

    setDeleting(true);
    try {
      const res = await forgeApi.deleteStorySpawn(spawn.storyId, deleteBaseBranch);
      const baseTxt = res.baseBranchDeleted ? ` (base '${spawn.baseBranch}' removed)` : '';
      toast(`Deleted spawn '${spawn.storyTitle}'${baseTxt}`, 'success');
      onDeleted?.(spawn.storyId);
    } catch (err) {
      if (err instanceof ForgeApiError && err.status === 409 && deleteBaseBranch) {
        // Shared base branch: retry without the flag so the spawn still
        // gets removed; the server's detail explains which spawns share
        // the base.
        try {
          await forgeApi.deleteStorySpawn(spawn.storyId, false);
          const detailMsg = err.problem?.detail
            ?? `Base branch '${spawn.baseBranch}' is shared and was kept.`;
          toast(`Deleted spawn (base branch kept): ${detailMsg}`, 'info');
          onDeleted?.(spawn.storyId);
          return;
        } catch (retryErr) {
          toast(`Delete failed: ${retryErr instanceof Error ? retryErr.message : String(retryErr)}`, 'danger');
        }
      } else {
        toast(`Delete failed: ${err instanceof Error ? err.message : String(err)}`, 'danger');
      }
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div
      className={`story-row phase-${spawn.phase}`}
      onClick={() => navigate(`/workstreams/${encodeURIComponent(spawn.storyId)}`)}
    >
      <div className="sr-num">{String(index + 1).padStart(2, '0')}</div>
      <div className="sr-meta">
        <div className="sr-id">{spawn.storyId}</div>
        <div className="sr-title">{spawn.storyTitle}</div>
        <div className="sr-sub">
          <span className="seg"><b>{wsCount}</b> workstreams</span>
          {mergingCount > 0 && <span className="seg" style={{ color: 'var(--sc-cyan)' }}><b>{mergingCount}</b> merging</span>}
          {blockedCount > 0 && <span className="seg" style={{ color: 'var(--sc-red)' }}><b>{blockedCount}</b> blocked</span>}
          {completeCount > 0 && <span className="seg" style={{ color: 'var(--sc-green)' }}><b>{completeCount}</b> complete</span>}
          {spawn.repoId && <span className="seg">repo <b>{spawn.repoId}</b></span>}
        </div>
        {detail && <StoryDeliveryStrip detail={detail} />}
      </div>
      <span className={`phase-badge ${spawn.phase}`}>{spawn.phase}</span>
      <div className="sr-progress">
        <div className="label">{phaseIdx + 1}/{PHASES.length} phases</div>
        <div className="bar"><div className="fill" style={{ transform: `scaleX(${pct / 100})` }} /></div>
        <div className="pct">{pct}%</div>
      </div>
      <div className={`sr-score${score == null ? '' : score >= 90 ? ' pass' : score >= 75 ? ' warn' : ' fail'}`}>
        <div className="v">{score == null ? '—' : score}</div>
        <div className="k">SCORE</div>
      </div>
      <button
        type="button"
        className="sr-delete"
        title={deleting ? 'Deleting…' : 'Delete spawn (wipes workstreams, PRs, branches)'}
        aria-label={`Delete spawn ${spawn.storyTitle}`}
        disabled={!detail || deleting}
        onClick={handleDelete}
      >
        {deleting ? (
          '…'
        ) : (
          // Inline SVG trash-can — renders consistently across fonts and
          // matches the terminal aesthetic better than an emoji glyph that
          // would fall through to a colored system emoji on Windows.
          <svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M3 4.5h10" />
            <path d="M6.5 4.5V3a1 1 0 0 1 1-1h1a1 1 0 0 1 1 1v1.5" />
            <path d="M4.5 4.5v8.5a1.5 1.5 0 0 0 1.5 1.5h4a1.5 1.5 0 0 0 1.5-1.5V4.5" />
            <path d="M7 7v5" />
            <path d="M9 7v5" />
          </svg>
        )}
      </button>
    </div>
  );
}

