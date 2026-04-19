import type { ExecutionState } from './ExecutionIndicator';

export interface RunControlsProps {
  state: ExecutionState;
  onRun?: () => void;
  onPause?: () => void;
  onResume?: () => void;
  onRerun?: () => void;
  onCancel?: () => void;
}

export function RunControls({ state, onRun, onPause, onResume, onRerun, onCancel }: RunControlsProps) {
  const isRunning = state === 'running';
  const isPaused = state === 'paused';
  const isFinished = state === 'success' || state === 'failed' || state === 'cancelled';

  return (
    <div className="tb-group" data-testid="run-controls">
      <button
        className="tb-btn"
        onClick={onRun}
        disabled={isRunning || isPaused}
        title="Run (start fresh)"
        data-testid="btn-run"
      >
        <svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>
        Run
      </button>
      <button
        className="tb-btn"
        onClick={onPause}
        disabled={!isRunning}
        title="Pause"
        data-testid="btn-pause"
      >
        <svg viewBox="0 0 24 24" fill="currentColor"><path d="M6 4h4v16H6zM14 4h4v16h-4z" /></svg>
        Pause
      </button>
      <button
        className="tb-btn"
        onClick={onResume}
        disabled={!(isPaused || state === 'failed' || state === 'cancelled')}
        title="Resume"
        data-testid="btn-resume"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M1 4v6h6M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
        </svg>
        Resume
      </button>
      <button
        className="tb-btn"
        onClick={onRerun}
        disabled={!isFinished}
        title="Rerun from beginning"
        data-testid="btn-rerun"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
        </svg>
        Rerun
      </button>
      <button
        className="tb-btn danger"
        onClick={onCancel}
        disabled={!(isRunning || isPaused)}
        title="Cancel"
        data-testid="btn-cancel"
      >
        <svg viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" /></svg>
        Cancel
      </button>
    </div>
  );
}
