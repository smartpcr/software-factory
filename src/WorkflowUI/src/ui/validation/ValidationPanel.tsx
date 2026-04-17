export interface ValidationEntry {
  nodeId: string;
  field?: string;
  message: string;
  severity: 'error' | 'warning';
}

export interface ValidationPanelProps {
  entries: ValidationEntry[];
  onJumpToNode: (nodeId: string) => void;
}

export function ValidationPanel({ entries, onJumpToNode }: ValidationPanelProps) {
  if (entries.length === 0) {
    return (
      <div data-testid="validation-panel-empty">
        <p>No validation issues</p>
      </div>
    );
  }

  return (
    <div data-testid="validation-panel">
      <ul>
        {entries.map((entry, idx) => (
          <li
            key={`${entry.nodeId}-${entry.field ?? ''}-${idx}`}
            data-testid={`validation-entry-${idx}`}
            data-severity={entry.severity}
          >
            <button
              data-testid={`jump-to-${entry.nodeId}`}
              onClick={() => onJumpToNode(entry.nodeId)}
              type="button"
            >
              {entry.nodeId}
            </button>
            {entry.field && <span data-testid="entry-field"> ({entry.field})</span>}
            <span data-testid="entry-message">: {entry.message}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
