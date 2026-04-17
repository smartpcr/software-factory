import { useCallback } from 'react';
import type { Connection } from '@xyflow/react';
import { useGraphStore } from '../state/graphStore';
import type { NodeConnectionDto } from '../../engine-contract';

export function useConnectionCreation() {
  const addConnection = useGraphStore((s) => s.addConnection);

  const onConnect = useCallback(
    (connection: Connection) => {
      if (!connection.source || !connection.target) return;

      const conn: NodeConnectionDto = {
        sourceNodeId: connection.source,
        targetNodeId: connection.target,
        triggerMessageType: 'Complete',
        isEnabled: true,
        priority: 0,
      };

      addConnection(conn);
    },
    [addConnection],
  );

  return { onConnect };
}