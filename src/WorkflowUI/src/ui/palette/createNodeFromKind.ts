import type { RuntimeType, NodeDto } from '../../engine-contract';

let nodeCounter = 0;

export function createNodeFromKind(runtimeType: RuntimeType): NodeDto {
  nodeCounter += 1;
  const nodeId = `node-${runtimeType.toLowerCase()}-${nodeCounter}`;

  const base = {
    nodeId,
    nodeName: `New ${runtimeType}`,
    runtimeType,
    joinType: 'Any' as const,
    maxConcurrentExecutions: 0,
    priority: 'Normal' as const,
  };

  switch (runtimeType) {
    case 'Noop':
      return { ...base, runtimeType: 'Noop' };
    case 'CSharp':
      return { ...base, runtimeType: 'CSharp' };
    case 'CSharpScript':
      return { ...base, runtimeType: 'CSharpScript', scriptPath: '', scriptContent: '' };
    case 'CSharpTask':
      return { ...base, runtimeType: 'CSharpTask' };
    case 'PowerShell':
      return { ...base, runtimeType: 'PowerShell', scriptPath: '' };
    case 'PowerShellTask':
      return { ...base, runtimeType: 'PowerShellTask' };
    case 'IfElse':
      return { ...base, runtimeType: 'IfElse', condition: '' };
    case 'ForEach':
      return { ...base, runtimeType: 'ForEach', collectionExpression: '', itemVariableName: 'item' };
    case 'While':
      return { ...base, runtimeType: 'While', conditionExpression: '', maxIterations: 1000 };
    case 'Switch':
      return { ...base, runtimeType: 'Switch', expression: '', cases: {} };
    case 'Subflow':
      return {
        ...base,
        runtimeType: 'Subflow',
        workflowFilePath: '',
        inputMappings: {},
        outputMappings: {},
        skipValidation: false,
        timeout: '00:05:00',
      };
    case 'Timer':
      return { ...base, runtimeType: 'Timer', schedule: '*/5 * * * *' };
    case 'Container':
      return { ...base, runtimeType: 'Container', executionMode: 'Sequential' };
    default:
      throw new Error(`Unknown runtime type: ${runtimeType}`);
  }
}

export function resetNodeCounter(): void {
  nodeCounter = 0;
}