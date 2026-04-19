import type { NodeExecutionStatus } from '../../engine-contract';

export interface StatusBadgeToken {
  label: string;
  color: string;
  bgColor: string;
  icon: string;
  cssClass: string;
}

export const STATUS_BADGE_TOKENS: Record<NodeExecutionStatus, StatusBadgeToken> = {
  Pending: { label: 'Pending', color: '#64748b', bgColor: '#f1f5f9', icon: '⏳', cssClass: 'pending' },
  Running: { label: 'Running', color: '#2563eb', bgColor: '#dbeafe', icon: '▶️', cssClass: 'running' },
  Completed: { label: 'Completed', color: '#16a34a', bgColor: '#dcfce7', icon: '✅', cssClass: 'success' },
  Failed: { label: 'Failed', color: '#dc2626', bgColor: '#fee2e2', icon: '❌', cssClass: 'failed' },
  Cancelled: { label: 'Cancelled', color: '#9333ea', bgColor: '#f3e8ff', icon: '🚫', cssClass: 'cancelled' },
  Skipped: { label: 'Skipped', color: '#71717a', bgColor: '#f4f4f5', icon: '⏭️', cssClass: 'skipped' },
};

export function getStatusBadgeToken(status: NodeExecutionStatus): StatusBadgeToken {
  return STATUS_BADGE_TOKENS[status];
}