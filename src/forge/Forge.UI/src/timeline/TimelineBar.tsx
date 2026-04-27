import type { TimelineBar as TimelineBarModel } from '@/types';

interface BarProps {
  bar: TimelineBarModel;
  startDay: number;
  dayW: number;
  forecast: boolean;
  onClick: (bar: TimelineBarModel) => void;
}

// Verbatim port of the prototype's bar block (lines 3750-3767).
export function TimelineBar({ bar, startDay, dayW, forecast, onClick }: BarProps) {
  const effectiveEnd = bar.planned && !forecast ? Math.max(bar.barStart, 0) : bar.barEnd;
  const left = (bar.barStart - startDay) * dayW;
  const width = (effectiveEnd - bar.barStart) * dayW;
  const span = bar.barEnd - bar.barStart;
  const progressEnd = span === 0 ? 0 : Math.max(0, Math.min(1, (0 - bar.barStart) / span));

  const cls = [
    'timeline-bar',
    bar.epicColor,
    bar.status === 'merging' ? 'merging' : '',
    bar.planned ? 'planned' : '',
  ].filter(Boolean).join(' ');

  return (
    <div
      className={cls}
      style={{ left: `${left}px`, width: `${Math.max(width, 0)}px` }}
      title={`${bar.summary}\nahead ${bar.ahead} · behind ${bar.behind}${bar.conflicts ? ` · ⚠ ${bar.conflicts} conflict${bar.conflicts > 1 ? 's' : ''}` : ''}\nagents: ${bar.agents.join(', ')}`}
      data-ws-id={bar.workstreamId}
      onClick={() => onClick(bar)}
    >
      <div className="timeline-bar-fill">
        <div className="timeline-bar-progress" style={{ width: `${progressEnd * 100}%` }} />
      </div>
      <span className="timeline-bar-label">{bar.name}</span>
    </div>
  );
}
