import type { RuntimeType, MessageType } from '../../engine-contract';
import { outgoingPortMatrix, incomingPortMatrix, type PortEntry } from '../../schemas/port-matrix';

export type ConnectionLegality = 'allowed' | 'forbidden' | 'warning';

export function isConnectionAllowed(
  sourceKind: RuntimeType,
  targetKind: RuntimeType,
  messageType: MessageType,
): ConnectionLegality {
  const outPorts = outgoingPortMatrix[sourceKind];
  const hasOutPort = outPorts?.some((p: PortEntry) => p.messageType === messageType);
  if (!hasOutPort) return 'forbidden';

  const inPorts = incomingPortMatrix[targetKind];
  const hasInPort = inPorts?.some((p: PortEntry) => p.messageType === messageType || p.messageType === 'Complete');
  if (!hasInPort) return 'forbidden';

  return 'allowed';
}

export function getOutgoingPorts(kind: RuntimeType): PortEntry[] {
  return outgoingPortMatrix[kind] ?? [];
}

export function getIncomingPorts(kind: RuntimeType): PortEntry[] {
  return incomingPortMatrix[kind] ?? [];
}