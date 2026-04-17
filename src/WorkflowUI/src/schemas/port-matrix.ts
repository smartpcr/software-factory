import type { RuntimeType, MessageType } from '../engine-contract';

export interface PortEntry {
  messageType: MessageType;
  label: string;
}

export const outgoingPortMatrix: Record<RuntimeType, PortEntry[]> = {
  Noop: [{ messageType: 'Complete', label: 'Done' }],
  CSharp: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  CSharpScript: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  CSharpTask: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  PowerShell: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  PowerShellTask: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  IfElse: [
    { messageType: 'Complete', label: 'True' },
    { messageType: 'Complete', label: 'False' },
  ],
  ForEach: [
    { messageType: 'Next', label: 'Body' },
    { messageType: 'Complete', label: 'Done' },
  ],
  While: [
    { messageType: 'Next', label: 'Body' },
    { messageType: 'Complete', label: 'Done' },
  ],
  Switch: [{ messageType: 'Complete', label: 'Case' }],
  Subflow: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
  Timer: [{ messageType: 'Complete', label: 'Fired' }],
  Container: [
    { messageType: 'Complete', label: 'Done' },
    { messageType: 'Fail', label: 'Error' },
  ],
};

export const incomingPortMatrix: Record<RuntimeType, PortEntry[]> = {
  Noop: [{ messageType: 'Complete', label: 'In' }],
  CSharp: [{ messageType: 'Complete', label: 'In' }],
  CSharpScript: [{ messageType: 'Complete', label: 'In' }],
  CSharpTask: [{ messageType: 'Complete', label: 'In' }],
  PowerShell: [{ messageType: 'Complete', label: 'In' }],
  PowerShellTask: [{ messageType: 'Complete', label: 'In' }],
  IfElse: [{ messageType: 'Complete', label: 'In' }],
  ForEach: [{ messageType: 'Complete', label: 'In' }],
  While: [{ messageType: 'Complete', label: 'In' }],
  Switch: [{ messageType: 'Complete', label: 'In' }],
  Subflow: [{ messageType: 'Complete', label: 'In' }],
  Timer: [{ messageType: 'Complete', label: 'In' }],
  Container: [{ messageType: 'Complete', label: 'In' }],
};

export function classifyOutgoing(
  runtimeType: RuntimeType,
): PortEntry[] {
  return outgoingPortMatrix[runtimeType] ?? [];
}
