import type { NodeConnectionDto, MessageType } from '../../../../engine-contract';

export const ALL_MESSAGE_TYPES: MessageType[] = [
  'Complete', 'Fail', 'Progress', 'Next', 'Cancel',
];

export const messageTypeEdges: NodeConnectionDto[] = ALL_MESSAGE_TYPES.map((mt) => ({
  sourceNodeId: 'source',
  targetNodeId: 'target',
  triggerMessageType: mt,
  isEnabled: true,
  priority: 0,
}));
