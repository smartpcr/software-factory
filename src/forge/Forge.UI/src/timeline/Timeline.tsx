import { forwardRef, useMemo } from 'react';
import type { Epic, TimelineBar as TimelineBarModel, TimelineLane, TimelineMarker } from '@/types';
import { TimelineAxis } from './TimelineAxis';
import { TimelineBar } from './TimelineBar';
import { TimelineEvent } from './TimelineEvent';
import { TimelineLegend } from './TimelineLegend';

interface TimelineProps {
  epics: Epic[];
  bars: TimelineBarModel[];
  markers: TimelineMarker[];
  startDay: number;
  endDay: number;
  dayW: number;
  forecast: boolean;
  lane: TimelineLane;
  collapsedEpics: Set<string>;
  onToggleEpic: (epicId: string) => void;
  onBarClick: (bar: TimelineBarModel) => void;
}

// Verbatim port of the prototype's renderTimeline (lines 3678-3797).
// Layout uses CSS in timeline.css; component only emits the same DOM
// structure (axis → epic-row → ws-row × N → legend) with the same classes.
export const Timeline = forwardRef<HTMLDivElement, TimelineProps>(function Timeline(
  { epics, bars, markers, startDay, endDay, dayW, forecast, lane, collapsedEpics, onToggleEpic, onBarClick },
  ref,
) {
  const totalDays = endDay - startDay;
  const trackW = totalDays * dayW;
  const todayPx = (0 - startDay) * dayW;

  const barsByEpic = useMemo(() => {
    const m = new Map<string, TimelineBarModel[]>();
    for (const b of bars) {
      (m.get(b.epic) ?? m.set(b.epic, []).get(b.epic)!).push(b);
    }
    return m;
  }, [bars]);

  const markersByWs = useMemo(() => {
    const m = new Map<string, TimelineMarker[]>();
    for (const ev of markers) {
      (m.get(ev.workstreamId) ?? m.set(ev.workstreamId, []).get(ev.workstreamId)!).push(ev);
    }
    return m;
  }, [markers]);

  return (
    <div className="timeline" id="timeline" ref={ref} style={{ '--day-w': `${dayW}px` } as React.CSSProperties}>
      <TimelineAxis startDay={startDay} endDay={endDay} dayW={dayW} />

      {epics.map((epic) => {
        const epicBars = barsByEpic.get(epic.id) ?? [];
        if (epicBars.length === 0) return null;
        const collapsed = collapsedEpics.has(epic.id);

        return (
          <FragmentRow key={epic.id}>
            <div className="timeline-row epic-row">
              <div
                className="timeline-lane-head"
                style={{ cursor: 'pointer' }}
                onClick={() => onToggleEpic(epic.id)}
                title={collapsed ? 'Expand' : 'Collapse'}
              >
                <span className="timeline-lane-marker" style={{ background: `var(--${epic.color})` }} />
                <div className="timeline-lane-info">
                  <div className="timeline-lane-id">{epic.id} · {epic.crew.length} agents</div>
                  <div className="timeline-lane-name">{epic.name}</div>
                </div>
              </div>
              <div className="timeline-track" style={{ minWidth: `${trackW}px` }}>
                <div className="timeline-today" style={{ left: `${todayPx}px` }} />
              </div>
            </div>

            {!collapsed && lane === 'workstream' && epicBars.map((bar) => {
              const evs = markersByWs.get(bar.workstreamId) ?? [];
              return (
                <div className="timeline-row ws-row" key={bar.workstreamId}>
                  <div className="timeline-lane-head">
                    <span
                      className="timeline-lane-marker"
                      style={{ background: `var(--${epic.color})`, opacity: 0.6 }}
                    />
                    <div className="timeline-lane-info">
                      <div className="timeline-lane-id">
                        {bar.agents.join(' · ')}{bar.conflicts > 0 ? ' · ⚠ conflict' : ''}
                      </div>
                      <div className="timeline-lane-name"><span className="ws-prefix">ws/</span>{bar.name}</div>
                    </div>
                  </div>
                  <div className="timeline-track" style={{ minWidth: `${trackW}px` }}>
                    <div className="timeline-today" style={{ left: `${todayPx}px` }} />
                    <TimelineBar bar={bar} startDay={startDay} dayW={dayW} forecast={forecast} onClick={onBarClick} />
                    {evs.map((ev, i) => (
                      <TimelineEvent
                        key={`${ev.workstreamId}-${ev.day}-${ev.kind}-${i}`}
                        event={ev}
                        startDay={startDay}
                        dayW={dayW}
                      />
                    ))}
                  </div>
                </div>
              );
            })}

            {!collapsed && lane === 'epic' && (
              // Epic-lane mode collapses every workstream into the epic row.
              // We re-render the bars on the epic track itself.
              <div className="timeline-row ws-row" key={`${epic.id}-rolled-up`}>
                <div className="timeline-lane-head">
                  <span className="timeline-lane-marker" style={{ background: `var(--${epic.color})`, opacity: 0.6 }} />
                  <div className="timeline-lane-info">
                    <div className="timeline-lane-id">{epicBars.length} workstreams</div>
                    <div className="timeline-lane-name">{epic.id}</div>
                  </div>
                </div>
                <div className="timeline-track" style={{ minWidth: `${trackW}px` }}>
                  <div className="timeline-today" style={{ left: `${todayPx}px` }} />
                  {epicBars.map((bar) => (
                    <TimelineBar key={bar.workstreamId} bar={bar} startDay={startDay} dayW={dayW} forecast={forecast} onClick={onBarClick} />
                  ))}
                </div>
              </div>
            )}
          </FragmentRow>
        );
      })}

      <TimelineLegend />
    </div>
  );
});

function FragmentRow({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
