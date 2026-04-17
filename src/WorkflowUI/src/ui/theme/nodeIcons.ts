import type { RuntimeType } from '../../engine-contract';

export interface NodeIconConfig {
  label: string;
  emoji: string;
  color: string;
  group: 'task' | 'control' | 'composition';
}

export const NODE_ICONS: Record<RuntimeType, NodeIconConfig> = {
  Noop: { label: 'Noop', emoji: '⊘', color: '#94a3b8', group: 'task' },
  CSharp: { label: 'C#', emoji: '🔷', color: '#7c3aed', group: 'task' },
  CSharpScript: { label: 'C# Script', emoji: '📝', color: '#8b5cf6', group: 'task' },
  CSharpTask: { label: 'C# Task', emoji: '⚙️', color: '#6d28d9', group: 'task' },
  PowerShell: { label: 'PowerShell', emoji: '🔵', color: '#2563eb', group: 'task' },
  PowerShellTask: { label: 'PS Task', emoji: '🔧', color: '#1d4ed8', group: 'task' },
  IfElse: { label: 'If/Else', emoji: '🔀', color: '#f59e0b', group: 'control' },
  ForEach: { label: 'ForEach', emoji: '🔁', color: '#10b981', group: 'control' },
  While: { label: 'While', emoji: '🔄', color: '#14b8a6', group: 'control' },
  Switch: { label: 'Switch', emoji: '🔀', color: '#eab308', group: 'control' },
  Subflow: { label: 'Subflow', emoji: '📦', color: '#ec4899', group: 'composition' },
  Timer: { label: 'Timer', emoji: '⏱️', color: '#f97316', group: 'composition' },
  Container: { label: 'Container', emoji: '📂', color: '#6366f1', group: 'composition' },
};

export function getNodeIcon(runtimeType: RuntimeType): NodeIconConfig {
  return NODE_ICONS[runtimeType];
}