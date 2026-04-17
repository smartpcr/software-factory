import type { MessageType } from '../../engine-contract';

export interface EdgeStyleToken {
  color: string;
  strokeWidth: number;
  dashArray?: string;
  label: string;
}

export const EDGE_STYLE_MAP: Record<MessageType, EdgeStyleToken> = {
  Complete: { color: '#16a34a', strokeWidth: 2, label: 'Complete' },
  Fail: { color: '#dc2626', strokeWidth: 2, dashArray: '5,5', label: 'Fail' },
  Progress: { color: '#2563eb', strokeWidth: 1.5, dashArray: '3,3', label: 'Progress' },
  Next: { color: '#f59e0b', strokeWidth: 2, label: 'Next' },
  Cancel: { color: '#9333ea', strokeWidth: 1.5, dashArray: '5,3', label: 'Cancel' },
};

export function getEdgeStyle(messageType: MessageType): EdgeStyleToken {
  return EDGE_STYLE_MAP[messageType];
}