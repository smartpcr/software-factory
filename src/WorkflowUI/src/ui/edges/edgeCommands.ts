import type { MessageType, NodeConnectionDto } from '../../engine-contract';
import { useGraphStore } from '../state/graphStore';

export function createConnection(
  sourceNodeId: string,
  targetNodeId: string,
  messageType: MessageType = 'Complete',
): NodeConnectionDto {
  const conn: NodeConnectionDto = {
    sourceNodeId,
    targetNodeId,
    triggerMessageType: messageType,
    isEnabled: true,
    priority: 0,
  };
  useGraphStore.getState().addConnection(conn);
  return conn;
}

export function deleteConnection(sourceNodeId: string, targetNodeId: string): void {
  useGraphStore.getState().removeConnection(sourceNodeId, targetNodeId);
}