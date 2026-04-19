import type { NodeTypes } from '@xyflow/react';
import { WorkflowNode } from '../nodes/WorkflowNode';
import { ContainerNode } from '../nodes/ContainerNode';
import { BranchNode } from '../nodes/BranchNode';

export const nodeTypes: NodeTypes = {
  workflowNode: WorkflowNode,
  containerNode: ContainerNode,
  branchNode: BranchNode,
};