import type { NodeExecutionStatus } from '../../../../engine-contract';

export const ALL_STATUSES: NodeExecutionStatus[] = [
  'Pending', 'Running', 'Completed', 'Failed', 'Cancelled', 'Skipped',
];

export const statusFixtures: Array<{ status: NodeExecutionStatus; nodeId: string }> = [
  { status: 'Pending', nodeId: 'node-1' },
  { status: 'Running', nodeId: 'node-2' },
  { status: 'Completed', nodeId: 'node-3' },
  { status: 'Failed', nodeId: 'node-4' },
  { status: 'Cancelled', nodeId: 'node-5' },
  { status: 'Skipped', nodeId: 'node-6' },
];
