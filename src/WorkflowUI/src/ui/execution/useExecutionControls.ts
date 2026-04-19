import { useSyncExternalStore, useCallback } from 'react';
import { ExecutionManager, type ExecutionState } from './ExecutionManager';

export function useExecutionControls() {
  const state: ExecutionState = useSyncExternalStore(
    ExecutionManager.subscribe,
    ExecutionManager.getState,
    () => 'idle' as ExecutionState,
  );

  const run = useCallback(() => ExecutionManager.run(), []);
  const pause = useCallback(() => ExecutionManager.pause(), []);
  const resume = useCallback(() => ExecutionManager.resume(), []);
  const rerun = useCallback(() => ExecutionManager.rerun(), []);
  const cancel = useCallback(() => ExecutionManager.cancel(), []);

  return { state, run, pause, resume, rerun, cancel };
}
