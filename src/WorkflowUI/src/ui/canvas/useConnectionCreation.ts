import { useCallback } from 'react';
import type { Connection } from '@xyflow/react';
import { useGraphStore } from '../state/graphStore';
import type { NodeConnectionDto, NodeDto } from '../../engine-contract';
import { validateConnection } from '../validation/connectionRules';

export function useConnectionCreation() {
  const addConnection = useGraphStore((s) => s.addConnection);
  const nodes = useGraphStore((s) => s.nodes);

  const onConnect = useCallback(
    (connection: Connection) => {
      if (!connection.source || !connection.target) return;

      const sourceNode = nodes.find((n: NodeDto) => n.nodeId === connection.source);
      const targetNode = nodes.find((n: NodeDto) => n.nodeId === connection.target);
      if (!sourceNode || !targetNode) return;

      const validation = validateConnection(sourceNode, targetNode, 'Complete');
      if (!validation.valid) return;

      const conn: NodeConnectionDto = {
        sourceNodeId: connection.source,
        targetNodeId: connection.target,
        triggerMessageType: 'Complete',
        isEnabled: true,
        priority: 0,
      };

      addConnection(conn);
    },
    [addConnection, nodes],
  );

  return { onConnect };
}