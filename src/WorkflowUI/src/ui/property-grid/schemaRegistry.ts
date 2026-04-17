import { z } from 'zod';
import type { RuntimeType } from '../../engine-contract';
import { RUNTIME_TYPES } from '../../engine-contract';
import { noopNodeSchema } from '../../schemas/node-noop.schema';
import { csharpNodeSchema } from '../../schemas/node-csharp.schema';
import { csharpScriptNodeSchemaBase } from '../../schemas/node-csharp-script.schema';
import { csharpTaskNodeSchema } from '../../schemas/node-csharp-task.schema';
import { powershellNodeSchema } from '../../schemas/node-powershell.schema';
import { powershellTaskNodeSchema } from '../../schemas/node-powershell-task.schema';
import { ifElseNodeSchema } from '../../schemas/node-ifelse.schema';
import { forEachNodeSchema } from '../../schemas/node-foreach.schema';
import { whileNodeSchema } from '../../schemas/node-while.schema';
import { switchNodeSchema } from '../../schemas/node-switch.schema';
import { timerNodeSchema } from '../../schemas/node-timer.schema';
import { subflowNodeSchema, containerNodeSchema } from '../../schemas/node-union';
import {
  baseFieldDescriptors,
  kindFieldDescriptors,
  type FieldDescriptor,
} from './fieldDescriptors';

const schemaMap: Record<RuntimeType, z.ZodType> = {
  Noop: noopNodeSchema,
  CSharp: csharpNodeSchema,
  CSharpScript: csharpScriptNodeSchemaBase,
  CSharpTask: csharpTaskNodeSchema,
  PowerShell: powershellNodeSchema,
  PowerShellTask: powershellTaskNodeSchema,
  IfElse: ifElseNodeSchema,
  ForEach: forEachNodeSchema,
  While: whileNodeSchema,
  Switch: switchNodeSchema,
  Timer: timerNodeSchema,
  Subflow: subflowNodeSchema,
  Container: containerNodeSchema,
};

export function getSchemaForRuntimeType(rt: RuntimeType): z.ZodType {
  return schemaMap[rt];
}

export function getFieldDescriptorsForRuntimeType(rt: RuntimeType): FieldDescriptor[] {
  return [...baseFieldDescriptors(), ...kindFieldDescriptors(rt)];
}

/** Verify that every runtime type has a schema entry at import time. */
export function allRuntimeTypesRegistered(): boolean {
  return RUNTIME_TYPES.every((rt) => rt in schemaMap);
}
