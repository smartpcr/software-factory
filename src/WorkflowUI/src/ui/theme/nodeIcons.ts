import type { RuntimeType } from '../../engine-contract';
import { getNodeTypeIcon } from './icons';
import type { SvgIconProps } from './icons';

export interface NodeIconConfig {
  label: string;
  emoji: string;
  color: string;
  cssColor: string;
  group: 'task' | 'control' | 'composition';
  Icon: React.FC<SvgIconProps>;
}

export const NODE_ICONS: Record<RuntimeType, NodeIconConfig> = {
  Noop: { label: 'Noop', emoji: '⊘', color: '#94a3b8', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('Noop') },
  CSharp: { label: 'C#', emoji: '🔷', color: '#7c3aed', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('CSharp') },
  CSharpScript: { label: 'C# Script', emoji: '📝', color: '#8b5cf6', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('CSharpScript') },
  CSharpTask: { label: 'C# Task', emoji: '⚙️', color: '#6d28d9', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('CSharpTask') },
  PowerShell: { label: 'PowerShell', emoji: '🔵', color: '#2563eb', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('PowerShell') },
  PowerShellTask: { label: 'PS Task', emoji: '🔧', color: '#1d4ed8', cssColor: 'var(--n-action)', group: 'task', Icon: getNodeTypeIcon('PowerShellTask') },
  IfElse: { label: 'If/Else', emoji: '🔀', color: '#f59e0b', cssColor: 'var(--n-if)', group: 'control', Icon: getNodeTypeIcon('IfElse') },
  ForEach: { label: 'ForEach', emoji: '🔁', color: '#10b981', cssColor: 'var(--n-loop)', group: 'control', Icon: getNodeTypeIcon('ForEach') },
  While: { label: 'While', emoji: '🔄', color: '#14b8a6', cssColor: 'var(--n-loop)', group: 'control', Icon: getNodeTypeIcon('While') },
  Switch: { label: 'Switch', emoji: '🔀', color: '#eab308', cssColor: 'var(--n-switch)', group: 'control', Icon: getNodeTypeIcon('Switch') },
  Subflow: { label: 'Subflow', emoji: '📦', color: '#ec4899', cssColor: 'var(--n-subflow)', group: 'composition', Icon: getNodeTypeIcon('Subflow') },
  Timer: { label: 'Timer', emoji: '⏱️', color: '#f97316', cssColor: 'var(--n-action)', group: 'composition', Icon: getNodeTypeIcon('Timer') },
  Container: { label: 'Container', emoji: '📂', color: '#6366f1', cssColor: 'var(--n-container)', group: 'composition', Icon: getNodeTypeIcon('Container') },
};

export function getNodeIcon(runtimeType: RuntimeType): NodeIconConfig {
  return NODE_ICONS[runtimeType];
}