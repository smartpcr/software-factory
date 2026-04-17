// GENERATED. Do not edit. Run tools/GenContract to regenerate.
// Engine assembly version: 0.5.0.0

export type ExecutionMode =
  | 'Sequential'
  | 'Parallel';

export type JoinType =
  | 'Any'
  | 'All';

export type MessageType =
  | 'Complete'
  | 'Fail'
  | 'Progress'
  | 'Next'
  | 'Cancel';

export type NodeExecutionStatus =
  | 'Pending'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'
  | 'Skipped';

export type NodePriority =
  | 'Low'
  | 'Normal'
  | 'High';

export type RuntimeType =
  | 'Noop'
  | 'CSharp'
  | 'CSharpScript'
  | 'CSharpTask'
  | 'PowerShell'
  | 'PowerShellTask'
  | 'IfElse'
  | 'ForEach'
  | 'While'
  | 'Switch'
  | 'Subflow'
  | 'Timer'
  | 'Container';

export type WorkflowExecutionStatus =
  | 'Pending'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'
  | 'Paused';

export type RetryStrategy =
  | 'None'
  | 'Fixed'
  | 'Exponential'
  | 'Linear';

export interface RetryPolicyDto {
  doNotRetryOn: string[];
  initialDelay: string; // ISO-8601 duration
  maxAttempts: number;
  maxDelay: string; // ISO-8601 duration
  multiplier: number;
  retryOn: string[];
  strategy: RetryStrategy;
}

export interface CircuitBreakerPolicyDto {
  failureThreshold: number;
  halfOpenSuccesses: number;
  minimumThroughput: number;
  openDuration: string; // ISO-8601 duration
}

export interface NodeDtoBase {
  runtimeType: RuntimeType;
  circuitBreakerPolicy?: CircuitBreakerPolicyDto;
  compensationNodeId?: string;
  configuration?: Record<string, unknown>;
  description?: string;
  fallbackNodeId?: string;
  joinType: JoinType;
  maxConcurrentExecutions: number;
  nodeId: string;
  nodeName: string;
  priority: NodePriority;
  retryPolicy?: RetryPolicyDto;
}

export interface CSharpNodeDto extends NodeDtoBase {
  runtimeType: 'CSharp';
  assemblyPath?: string;
  typeName?: string;
}

export interface CSharpScriptNodeDto extends NodeDtoBase {
  runtimeType: 'CSharpScript';
  scriptContent: string;
  scriptPath: string;
}

export interface CSharpTaskNodeDto extends NodeDtoBase {
  runtimeType: 'CSharpTask';
  executorAssemblyPath?: string;
  executorTypeName?: string;
  scriptContent?: string;
}

export interface ContainerNodeDto extends NodeDtoBase {
  runtimeType: 'Container';
  childConnections?: NodeConnectionDto[];
  childNodes?: NodeDto[];
  executionMode: ExecutionMode;
}

export interface ForEachNodeDto extends NodeDtoBase {
  runtimeType: 'ForEach';
  collectionExpression: string;
  itemVariableName: string;
}

export interface IfElseNodeDto extends NodeDtoBase {
  runtimeType: 'IfElse';
  condition: string;
}

export interface NoopNodeDto extends NodeDtoBase {
  runtimeType: 'Noop';
}

export interface PowerShellScriptNodeDto extends NodeDtoBase {
  runtimeType: 'PowerShell';
  modulePaths?: Record<string, string>;
  requiredModules?: string[];
  scriptPath: string;
}

export interface PowerShellTaskNodeDto extends NodeDtoBase {
  runtimeType: 'PowerShellTask';
  modulePaths?: Record<string, string>;
  requiredModules?: string[];
  scriptContent?: string;
  scriptPath?: string;
}

export interface SubflowNodeDto extends NodeDtoBase {
  runtimeType: 'Subflow';
  inputMappings: Record<string, string>;
  outputMappings: Record<string, string>;
  skipValidation: boolean;
  timeout: string; // ISO-8601 duration
  workflowDefinition?: WorkflowDefinitionDto;
  workflowFilePath: string;
}

export interface SwitchNodeDto extends NodeDtoBase {
  runtimeType: 'Switch';
  cases: Record<string, string>;
  expression: string;
}

export interface TimerNodeDto extends NodeDtoBase {
  runtimeType: 'Timer';
  schedule: string;
}

export interface WhileNodeDto extends NodeDtoBase {
  runtimeType: 'While';
  conditionExpression: string;
  maxIterations: number;
}

export type NodeDto =
  | CSharpNodeDto
  | CSharpScriptNodeDto
  | CSharpTaskNodeDto
  | ContainerNodeDto
  | ForEachNodeDto
  | IfElseNodeDto
  | NoopNodeDto
  | PowerShellScriptNodeDto
  | PowerShellTaskNodeDto
  | SubflowNodeDto
  | SwitchNodeDto
  | TimerNodeDto
  | WhileNodeDto;

export interface NodeConnectionDto {
  condition?: string;
  isEnabled: boolean;
  metadata?: Record<string, unknown>;
  priority: number;
  sourceNodeId: string;
  sourcePort?: string;
  targetNodeId: string;
  targetPort?: string;
  triggerMessageType: MessageType;
}

export interface WorkflowDefinitionDto {
  workflowId?: string;
  workflowName?: string;
  description?: string;
  version?: string;
  nodes?: NodeDto[];
  connections?: NodeConnectionDto[];
  metadata?: Record<string, string>;
  entryPointNodeId?: string;
  maxConcurrency?: number;
  allowPause?: boolean;
  timeoutSeconds?: number;
  defaultVariables?: Record<string, string>;
}

