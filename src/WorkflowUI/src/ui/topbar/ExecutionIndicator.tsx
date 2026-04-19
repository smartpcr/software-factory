export type ExecutionState= 'idle' | 'running' | 'paused' | 'success' | 'failed' | 'cancelled';

export interface ExecutionIndicatorProps {
  state: ExecutionState;
}

export function ExecutionIndicator({ state }: ExecutionIndicatorProps) {
  return (
    <div className={`exec-indicator ${state}`} data-testid="exec-indicator">
      <span className="pulse-dot" />
      <span>{state.toUpperCase()}</span>
    </div>
  );
}
