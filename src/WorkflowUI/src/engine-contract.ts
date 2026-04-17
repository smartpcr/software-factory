/**
 * Engine contract facade.
 *
 * All UI code imports engine types through this module, never
 * from engine-contract.generated.ts directly. ESLint enforces this.
 */

// Re-export all generated types
export type {
  ExecutionMode,
  JoinType,
  MessageType,
  NodeExecutionStatus,
  NodePriority,
  RuntimeType,
  WorkflowExecutionStatus,
  RetryPolicyDto,
  CircuitBreakerPolicyDto,
  NodeDtoBase,
  CSharpNodeDto,
  CSharpScriptNodeDto,
  CSharpTaskNodeDto,
  ContainerNodeDto,
  ForEachNodeDto,
  IfElseNodeDto,
  NoopNodeDto,
  PowerShellScriptNodeDto,
  PowerShellTaskNodeDto,
  SubflowNodeDto,
  SwitchNodeDto,
  TimerNodeDto,
  WhileNodeDto,
  NodeDto,
  NodeConnectionDto,
  WorkflowDefinitionDto,
} from './engine-contract.generated';

// Re-export assertNever helper
export { assertNever } from './contract/assert-never';

// UI-only helpers

import type {
  NodeDto,
  ContainerNodeDto,
  SwitchNodeDto,
  IfElseNodeDto,
  ForEachNodeDto,
  WhileNodeDto,
  SubflowNodeDto,
} from './engine-contract.generated';

/**
 * All RuntimeType values in engine source-declaration order.
 * This is a readonly tuple so adding an engine kind without updating
 * this array is a type error.
 */
export const RUNTIME_TYPES = [
  'Noop',
  'CSharp',
  'CSharpScript',
  'CSharpTask',
  'PowerShell',
  'PowerShellTask',
  'IfElse',
  'ForEach',
  'While',
  'Switch',
  'Subflow',
  'Timer',
  'Container',
] as const;

// Narrowing type guards

export function isContainerNode(n: NodeDto): n is ContainerNodeDto {
  return n.runtimeType === 'Container';
}

export function isSwitchNode(n: NodeDto): n is SwitchNodeDto {
  return n.runtimeType === 'Switch';
}

export function isIfElseNode(n: NodeDto): n is IfElseNodeDto {
  return n.runtimeType === 'IfElse';
}

export function isForEachNode(n: NodeDto): n is ForEachNodeDto {
  return n.runtimeType === 'ForEach';
}

export function isWhileNode(n: NodeDto): n is WhileNodeDto {
  return n.runtimeType === 'While';
}

export function isSubflowNode(n: NodeDto): n is SubflowNodeDto {
  return n.runtimeType === 'Subflow';
}
