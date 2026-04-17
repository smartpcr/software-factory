import { useCallback } from 'react';
import { useReactFlow } from '@xyflow/react';

export function useFitWorkflow() {
  const reactFlow = useReactFlow();

  const fitView = useCallback(() => {
    reactFlow.fitView({ padding: 0.2, duration: 300 });
  }, [reactFlow]);

  return { fitView };
}