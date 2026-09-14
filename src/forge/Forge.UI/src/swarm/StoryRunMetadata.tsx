import { useEffect, useState } from 'react';
import type { StorySpawn } from '@/api/forgeApi';

const startTimeFormat = new Intl.DateTimeFormat(undefined, {
  year: 'numeric', month: 'short', day: 'numeric',
  hour: '2-digit', minute: '2-digit', timeZoneName: 'short',
});

function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const seconds = Math.floor(ms / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);
  if (days > 0) return `${days}d ${hours % 24}h ${minutes % 60}m`;
  if (hours > 0) return `${hours}h ${minutes % 60}m`;
  if (minutes > 0) return `${minutes}m ${seconds % 60}s`;
  return `${seconds}s`;
}

/** Keep the elapsed clock local so its ticks don't redraw the story's DAG. */
export function StoryRunMetadata({ spawn }: {
  spawn: Pick<StorySpawn, 'createdAt' | 'updatedAt' | 'phase' | 'baseBranch'>;
}) {
  const start = Date.parse(spawn.createdAt);
  const hasStart = Number.isFinite(start);
  const done = spawn.phase === 'done';
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (done || !hasStart) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [done, hasStart, spawn.createdAt]);

  // Match the story detail's SPAWN duration: createdAt is the start;
  // done rows use updatedAt because the contract has no completedAt.
  const end = done ? Date.parse(spawn.updatedAt) : now;
  const targetBranch = spawn.baseBranch || 'main';

  return (
    <dl className="sr-run-meta" aria-label="Story run details">
      <div>
        <dt>Started</dt>
        <dd title="When this story spawn was created, in your local time zone">
          {hasStart ? (
            <time dateTime={spawn.createdAt} title={new Date(start).toLocaleString()}>
              {startTimeFormat.format(start)}
            </time>
          ) : '—'}
        </dd>
      </div>
      <div>
        <dt>Duration</dt>
        <dd title={done
          ? 'Spawn creation to its last recorded update when done'
          : 'Elapsed since spawn creation, including waiting and paused time'}>
          {formatDuration(end - start)}
        </dd>
      </div>
      <div className="sr-target-branch">
        <dt>Target branch</dt>
        <dd><code title={targetBranch}>{targetBranch}</code></dd>
      </div>
    </dl>
  );
}