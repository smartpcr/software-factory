import type { RuntimeType } from '../../engine-contract';

export const DRAG_TYPE = 'application/workflow-node-kind';

export interface DragPayload {
  runtimeType: RuntimeType;
}

export function encodeDragPayload(payload: DragPayload): string {
  return JSON.stringify(payload);
}

export function decodeDragPayload(data: string): DragPayload | null {
  try {
    const parsed = JSON.parse(data) as DragPayload;
    if (typeof parsed.runtimeType === 'string') return parsed;
    return null;
  } catch {
    return null;
  }
}