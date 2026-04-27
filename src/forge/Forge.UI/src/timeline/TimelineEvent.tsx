import type { TimelineMarker } from '@/types';

interface EventProps {
  event: TimelineMarker;
  startDay: number;
  dayW: number;
}

// Verbatim port of the prototype's event-marker block (lines 3768-3775).
export function TimelineEvent({ event, startDay, dayW }: EventProps) {
  const left = (event.day - startDay) * dayW;
  return (
    <div
      className={`timeline-event ${event.kind}`}
      style={{ left: `${left}px` }}
      title={event.label}
      data-kind={event.kind}
    />
  );
}
