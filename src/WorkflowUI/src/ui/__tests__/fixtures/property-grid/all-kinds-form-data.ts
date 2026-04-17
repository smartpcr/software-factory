import type { NodeDto } from '../../../../engine-contract';

export const noopFixture: NodeDto = {
  runtimeType: 'Noop',
  nodeId: 'noop-1',
  nodeName: 'NoopNode',
  description: 'A no-op node',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
};

export const csharpFixture: NodeDto = {
  runtimeType: 'CSharp',
  nodeId: 'csharp-1',
  nodeName: 'CSharpNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  assemblyPath: '/path/to/assembly.dll',
  typeName: 'MyNamespace.MyExecutor',
};

export const csharpScriptFixture: NodeDto = {
  runtimeType: 'CSharpScript',
  nodeId: 'csharp-script-1',
  nodeName: 'CSharpScriptNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  scriptPath: '',
  scriptContent: 'Console.WriteLine("Hello");',
};

export const csharpTaskFixture: NodeDto = {
  runtimeType: 'CSharpTask',
  nodeId: 'csharp-task-1',
  nodeName: 'CSharpTaskNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  scriptContent: 'return Task.CompletedTask;',
};

export const powershellFixture: NodeDto = {
  runtimeType: 'PowerShell',
  nodeId: 'ps-1',
  nodeName: 'PowerShellNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  scriptPath: './scripts/run.ps1',
};

export const powershellTaskFixture: NodeDto = {
  runtimeType: 'PowerShellTask',
  nodeId: 'ps-task-1',
  nodeName: 'PowerShellTaskNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
};

export const ifElseFixture: NodeDto = {
  runtimeType: 'IfElse',
  nodeId: 'ifelse-1',
  nodeName: 'IfElseNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  condition: 'x > 0',
};

export const forEachFixture: NodeDto = {
  runtimeType: 'ForEach',
  nodeId: 'foreach-1',
  nodeName: 'ForEachNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  collectionExpression: 'items',
  itemVariableName: 'item',
};

export const whileFixture: NodeDto = {
  runtimeType: 'While',
  nodeId: 'while-1',
  nodeName: 'WhileNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  conditionExpression: 'count < 10',
  maxIterations: 1000,
};

export const switchFixture: NodeDto = {
  runtimeType: 'Switch',
  nodeId: 'switch-1',
  nodeName: 'SwitchNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  expression: 'status',
  cases: { ok: 'handle-ok', error: 'handle-error' },
};

export const subflowFixture: NodeDto = {
  runtimeType: 'Subflow',
  nodeId: 'subflow-1',
  nodeName: 'SubflowNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  workflowFilePath: './workflows/sub.yaml',
  inputMappings: {},
  outputMappings: {},
  timeout: '00:05:00',
  skipValidation: false,
};

export const timerFixture: NodeDto = {
  runtimeType: 'Timer',
  nodeId: 'timer-1',
  nodeName: 'TimerNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  schedule: '0 * * * *',
};

export const containerFixture: NodeDto = {
  runtimeType: 'Container',
  nodeId: 'container-1',
  nodeName: 'ContainerNode',
  joinType: 'Any',
  priority: 'Normal',
  maxConcurrentExecutions: 0,
  executionMode: 'Sequential',
};

export const allKindFixtures: NodeDto[] = [
  noopFixture,
  csharpFixture,
  csharpScriptFixture,
  csharpTaskFixture,
  powershellFixture,
  powershellTaskFixture,
  ifElseFixture,
  forEachFixture,
  whileFixture,
  switchFixture,
  subflowFixture,
  timerFixture,
  containerFixture,
];
