import type { MessageType } from '../../engine-contract';

export interface EdgeStyleToken {
  color: string;
  cssColor: string;
  strokeWidth: number;
  dashArray?: string;
  label: string;
  cssClass: string;
}

export const EDGE_STYLE_MAP: Record<MessageType, EdgeStyleToken> = {
  Complete: { color: '#16a34a', cssColor: 'var(--stroke-3)', strokeWidth: 1.6, label: 'Complete', cssClass: '' },
  Fail: { color: '#dc2626', cssColor: 'var(--err)', strokeWidth: 1.6, dashArray: '4,3', label: 'Fail', cssClass: 'error-edge' },
  Progress: { color: '#2563eb', cssColor: 'var(--paused)', strokeWidth: 1.4, dashArray: '3,3', label: 'Progress', cssClass: '' },
  Next: { color: '#f59e0b', cssColor: 'var(--accent)', strokeWidth: 1.6, label: 'Next', cssClass: '' },
  Cancel: { color: '#9333ea', cssColor: 'var(--cancel)', strokeWidth: 1.4, dashArray: '5,3', label: 'Cancel', cssClass: '' },
};

export function getEdgeStyle(messageType: MessageType): EdgeStyleToken {
  return EDGE_STYLE_MAP[messageType];
}