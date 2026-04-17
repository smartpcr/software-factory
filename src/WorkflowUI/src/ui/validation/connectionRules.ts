import type { MessageType, NodeDto } from '../../engine-contract';
import { isConnectionAllowed } from '../ports/portMatrix';

export interface ConnectionValidation {
  valid: boolean;
  reason?: string;
}

export function validateConnection(
  sourceNode: NodeDto,
  targetNode: NodeDto,
  messageType: MessageType,
): ConnectionValidation {
  if (sourceNode.nodeId === targetNode.nodeId) {
    return { valid: false, reason: 'Cannot connect a node to itself.' };
  }

  const legality = isConnectionAllowed(
    sourceNode.runtimeType,
    targetNode.runtimeType,
    messageType,
  );

  if (legality === 'forbidden') {
    return {
      valid: false,
      reason: `${sourceNode.runtimeType} cannot send ${messageType} to ${targetNode.runtimeType}.`,
    };
  }

  return { valid: true };
}