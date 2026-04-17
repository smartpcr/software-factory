import type { WorkflowDefinitionDto, RuntimeType } from '../../../../engine-contract';

const KINDS: RuntimeType[] = [
  'Noop', 'CSharp', 'CSharpScript', 'CSharpTask',
  'PowerShell', 'PowerShellTask', 'IfElse', 'ForEach',
  'While', 'Switch', 'Subflow', 'Timer', 'Container',
];

function makeNode(kind: RuntimeType) {
  const base = {
    nodeId: `node-${kind.toLowerCase()}`,
    nodeName: `Test ${kind}`,
    runtimeType: kind,
    joinType: 'Any' as const,
    maxConcurrentExecutions: 0,
    priority: 'Normal' as const,
  };

  switch (kind) {
    case 'IfElse': return { ...base, runtimeType: kind, condition: 'true' };
    case 'ForEach': return { ...base, runtimeType: kind, collectionExpression: '[]', itemVariableName: 'item' };
    case 'While': return { ...base, runtimeType: kind, conditionExpression: 'false', maxIterations: 10 };
    case 'Switch': return { ...base, runtimeType: kind, expression: 'x', cases: {} };
    case 'Subflow': return { ...base, runtimeType: kind, workflowFilePath: 'child.yaml', inputMappings: {}, outputMappings: {}, skipValidation: false, timeout: '00:05:00' };
    case 'Timer': return { ...base, runtimeType: kind, schedule: '*/5 * * * *' };
    case 'Container': return { ...base, runtimeType: kind, executionMode: 'Sequential' as const };
    case 'CSharpScript': return { ...base, runtimeType: kind, scriptPath: '', scriptContent: '' };
    case 'PowerShell': return { ...base, runtimeType: kind, scriptPath: '' };
    default: return { ...base, runtimeType: kind };
  }
}

export const allKindsWorkflow: WorkflowDefinitionDto = {
  workflowId: 'all-kinds-test',
  workflowName: 'All Kinds Workflow',
  nodes: KINDS.map((kind) => makeNode(kind)) as WorkflowDefinitionDto['nodes'],
  connections: [],
};
