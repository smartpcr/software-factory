/**
 * Run-attach picker UI. Polls GET /api/runs every 5s while open.
 */
import { useEffect, useState, useCallback } from 'react';

export interface RunInfo {
  runId: string;
  workflowId: string;
  workflowName: string;
  status: string;
  startedAt: string;
}

export interface RunAttachPickerProps {
  isOpen: boolean;
  onClose: () => void;
  onSelectRun: (runId: string) => void;
  fetchRuns?: () => Promise<RunInfo[]>;
}

const DEFAULT_POLL_INTERVAL = 5000;

export function RunAttachPicker({
  isOpen,
  onClose,
  onSelectRun,
  fetchRuns,
}: RunAttachPickerProps) {
  const [runs, setRuns] = useState<RunInfo[]>([]);
  const [loading, setLoading] = useState(false);

  const loadRuns = useCallback(async () => {
    if (!fetchRuns) return;
    setLoading(true);
    try {
      const data = await fetchRuns();
      setRuns(data);
    } finally {
      setLoading(false);
    }
  }, [fetchRuns]);

  useEffect(() => {
    if (!isOpen) return;
    loadRuns();
    const interval = setInterval(loadRuns, DEFAULT_POLL_INTERVAL);
    return () => clearInterval(interval);
  }, [isOpen, loadRuns]);

  if (!isOpen) return null;

  return (
    <div className="run-attach-picker" role="dialog" aria-label="Select a run">
      <div className="picker-header">
        <h3>Active Runs</h3>
        <button onClick={onClose} aria-label="Close picker">
          ✕
        </button>
      </div>
      {loading && <div aria-live="polite">Loading...</div>}
      <ul role="listbox" aria-label="Available runs">
        {runs.map((run) => (
          <li
            key={run.runId}
            role="option"
            tabIndex={0}
            onClick={() => onSelectRun(run.runId)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') onSelectRun(run.runId);
            }}
          >
            <span className="run-name">{run.workflowName}</span>
            <span className="run-status">{run.status}</span>
          </li>
        ))}
        {!loading && runs.length === 0 && (
          <li className="no-runs">No active runs</li>
        )}
      </ul>
    </div>
  );
}
