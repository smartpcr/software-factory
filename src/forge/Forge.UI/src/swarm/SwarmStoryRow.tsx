import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { forgeApi, type StorySpawn, type StorySpawnDetail, type StorySpawnPhase } from '@/api/forgeApi';
import { useSpawnStore } from '@/state/useSpawnStore';

interface Props {
  spawn: StorySpawn;
  index: number;
}

const PHASES: StorySpawnPhase[] = ['plan', 'decompose', 'implement', 'review', 'qa', 'done'];

export function SwarmStoryRow({ spawn, index }: Props) {
  const navigate = useNavigate();
  const tick = useSpawnStore((s) => s.ticks[spawn.storyId] ?? 0);
  const [detail, setDetail] = useState<StorySpawnDetail | null>(null);

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
        {wsCount > 0 && (
          <div className="ws-strip" title={`${wsCount} workstreams`}>
            {ws.map((w) => (
              <span key={w.id} className={`ws-chip ${w.status}`} title={`${w.id} · ${w.status}`} />
            ))}
          </div>
        )}
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
    </div>
  );
}
