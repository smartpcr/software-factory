import type { WorkflowDefinitionDto } from '../../../../engine-contract';

export const emptyWorkflow: WorkflowDefinitionDto = {
  workflowId: 'empty-test',
  workflowName: 'Empty Workflow',
  nodes: [],
  connections: [],
};
