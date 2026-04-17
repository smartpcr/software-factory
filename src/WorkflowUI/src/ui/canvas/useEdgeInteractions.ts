import { useCallback } from 'react';
import { useGraphStore } from '../state/graphStore';

export function useEdgeInteractions() {
  const removeConnection = useGraphStore((s) => s.removeConnection);

  const onEdgeDelete = useCallback(
    (sourceNodeId: string, targetNodeId: string) => {
      removeConnection(sourceNodeId, targetNodeId);
    },
    [removeConnection],
  );

  return { onEdgeDelete };
}