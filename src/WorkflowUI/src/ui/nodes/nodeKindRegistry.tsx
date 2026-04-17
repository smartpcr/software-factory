import type { RuntimeType } from '../../engine-contract';
import { NODE_ICONS, type NodeIconConfig } from '../theme/nodeIcons';

export interface PaletteEntry {
  runtimeType: RuntimeType;
  icon: NodeIconConfig;
  description: string;
}

const DESCRIPTIONS: Record<RuntimeType, string> = {
  Noop: 'A no-operation node that passes through.',
  CSharp: 'Executes a C# class from an assembly.',
  CSharpScript: 'Runs inline or file-based C# script.',
  CSharpTask: 'Runs a C# task executor.',
  PowerShell: 'Runs a PowerShell script file.',
  PowerShellTask: 'Runs a PowerShell task.',
  IfElse: 'Conditional branch based on expression.',
  ForEach: 'Iterates over a collection.',
  While: 'Loops while condition is true.',
  Switch: 'Multi-way branch by expression.',
  Subflow: 'Invokes a nested workflow.',
  Timer: 'Fires on schedule (cron/interval).',
  Container: 'Groups child nodes for sequential/parallel execution.',
};

export function getRegistryEntries(): PaletteEntry[] {
  return (Object.keys(NODE_ICONS) as RuntimeType[]).map((rt) => ({
    runtimeType: rt,
    icon: NODE_ICONS[rt],
    description: DESCRIPTIONS[rt],
  }));
}

export function getRegistryEntry(runtimeType: RuntimeType): PaletteEntry {
  return {
    runtimeType,
    icon: NODE_ICONS[runtimeType],
    description: DESCRIPTIONS[runtimeType],
  };
}