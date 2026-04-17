/**
 * Event log side panel with filters by kind, nodeId, and messageType.
 */
import type { RunEvent, RunEventKind } from '../../telemetry/types';
import { useTelemetryStore, filterEvents } from '../store/telemetryStore';

export interface EventLogPanelProps {
  onEventSelect?: (event: RunEvent) => void;
}

const ALL_KINDS: RunEventKind[] = [
  'WorkflowStarted',
  'WorkflowCompleted',
  'WorkflowFailed',
  'WorkflowCancelled',
  'NodeStarted',
  'NodeCompleted',
  'NodeFailed',
  'NodeCancelled',
  'NodeProgress',
  'EdgeActivated',
  'NodeSkipped',
];

const MESSAGE_TYPES = ['Complete', 'Fail', 'Cancel'];

export function EventLogPanel({ onEventSelect }: EventLogPanelProps) {
  const events = useTelemetryStore((s) => s.events);
  const filters = useTelemetryStore((s) => s.filters);
  const setFilters = useTelemetryStore((s) => s.setFilters);

  const filteredEvents = filterEvents(events, filters);

  const handleKindToggle = (kind: RunEventKind) => {
    const next = new Set(filters.kinds);
    if (next.has(kind)) {
      next.delete(kind);
    } else {
      next.add(kind);
    }
    setFilters({ kinds: next });
  };

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
        <select
          aria-label="Filter by message type"
          value={filters.messageType ?? ''}
          onChange={(e) =>
            setFilters({ messageType: e.target.value || null })
          }
        >
          <option value="">All message types</option>
          {MESSAGE_TYPES.map((mt) => (
            <option key={mt} value={mt}>
              {mt}
            </option>
          ))}
        </select>
        <fieldset className="kind-filters" aria-label="Filter by event kind">
          {ALL_KINDS.map((kind) => (
            <label key={kind} className="kind-checkbox">
              <input
                type="checkbox"
                checked={filters.kinds.size === 0 || filters.kinds.has(kind)}
                onChange={() => handleKindToggle(kind)}
                aria-label={`Filter ${kind}`}
              />
              <span>{kind}</span>
            </label>
          ))}
        </fieldset>
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
}
