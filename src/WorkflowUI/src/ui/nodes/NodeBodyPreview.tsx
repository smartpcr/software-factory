import type { RuntimeType } from '../../engine-contract';

interface BodyRow {
  label: string;
  value: string;
}

function getPreviewRows(runtimeType: RuntimeType): BodyRow[] {
  switch (runtimeType) {
    case 'CSharp':
      return [{ label: 'type', value: 'C# Class' }];
    case 'CSharpScript':
      return [{ label: 'type', value: 'C# Script' }];
    case 'CSharpTask':
      return [{ label: 'type', value: 'C# Task' }];
    case 'PowerShell':
      return [{ label: 'type', value: 'PS Script' }];
    case 'PowerShellTask':
      return [{ label: 'type', value: 'PS Task' }];
    case 'IfElse':
      return [{ label: 'if', value: '—' }];
    case 'Switch':
      return [{ label: 'switch', value: '—' }];
    case 'ForEach':
      return [{ label: 'foreach', value: '—' }];
    case 'While':
      return [{ label: 'while', value: '—' }];
    case 'Subflow':
      return [{ label: 'flow', value: '—' }];
    case 'Timer':
      return [{ label: 'schedule', value: '—' }];
    case 'Container':
      return [{ label: 'mode', value: 'sequential' }];
    case 'Noop':
    default:
      return [{ label: 'type', value: 'Noop' }];
  }
}

export interface NodeBodyPreviewProps {
  runtimeType: RuntimeType;
}

export function NodeBodyPreview({ runtimeType }: NodeBodyPreviewProps) {
  const rows = getPreviewRows(runtimeType);

  return (
    <div className="node-body" data-testid="node-body-preview">
      {rows.map((row) => (
        <div className="node-body-row" key={row.label}>
          <span className="lab">{row.label}</span>
          <span className="val">{row.value}</span>
        </div>
      ))}
    </div>
  );
}
