/**
 * Event log side panel with filters by kind, nodeId, and messageType.
 */
import type { RunEvent } from '../../telemetry/types';
import { useTelemetryStore, filterEvents } from '../store/telemetryStore';

export interface EventLogPanelProps {
  onEventSelect?: (event: RunEvent) => void;
}

export function EventLogPanel({ onEventSelect }: EventLogPanelProps) {
  const events = useTelemetryStore((s) => s.events);
  const filters = useTelemetryStore((s) => s.filters);
  const setFilters = useTelemetryStore((s) => s.setFilters);

  const filteredEvents = filterEvents(events, filters);

  return (
    <div className="event-log-panel" role="log" aria-label="Event log">
      <div className="event-log-filters" role="search">
        <input
          type="text"
          placeholder="Filter by nodeId"
          aria-label="Filter by node ID"
          value={filters.nodeId ?? ''}
          onChange={(e) =>
            setFilters({ nodeId: e.target.value || null })
          }
        />
      </div>
      <div className="event-log-list" role="list">
        {filteredEvents.map((event) => (
          <div
            key={`${event.runId}-${event.seq}`}
            className={`event-row event-kind-${event.kind.toLowerCase()}`}
            role="listitem"
            tabIndex={0}
            onClick={() => onEventSelect?.(event)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') onEventSelect?.(event);
            }}
          >
            <span className="event-seq">#{event.seq}</span>
            <span className="event-kind">{event.kind}</span>
            {event.synthetic && (
              <span className="event-synthetic" aria-label="synthetic">
                ⊕
              </span>
            )}
            {event.nodeId && (
              <span className="event-node">{event.nodeId}</span>
            )}
          </div>
        ))}
      </div>
    </div>
  );
};
