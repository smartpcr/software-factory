import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';
import { noopNodeSchema } from './node-noop.schema';
import { csharpNodeSchema } from './node-csharp.schema';
import { csharpScriptNodeSchemaBase } from './node-csharp-script.schema';
import { csharpTaskNodeSchema } from './node-csharp-task.schema';
import { powershellNodeSchema } from './node-powershell.schema';
import { powershellTaskNodeSchema } from './node-powershell-task.schema';
import { ifElseNodeSchema } from './node-ifelse.schema';
import { forEachNodeSchema } from './node-foreach.schema';
import { whileNodeSchema } from './node-while.schema';
import { switchNodeSchema } from './node-switch.schema';
import { timerNodeSchema } from './node-timer.schema';
import { nodeConnectionSchema } from './node-connection.schema';
import type { RuntimeType } from '../engine-contract';

// Subflow schema — uses z.lazy to break recursion
const workflowDefinitionSchema: z.ZodType = z.lazy(() =>
  z.object({
    workflowId: z.string().optional(),
    workflowName: z.string().optional(),
    description: z.string().optional(),
    version: z.string().optional(),
    nodes: z.array(nodeDtoSchema).optional(),
    connections: z.array(nodeConnectionSchema).optional(),
    metadata: z.record(z.string(), z.string()).optional(),
    entryPointNodeId: z.string().optional(),
    maxConcurrency: z.number().int().optional(),
    allowPause: z.boolean().optional(),
    timeoutSeconds: z.number().optional(),
    defaultVariables: z.record(z.string(), z.string()).optional(),
  }),
);

const subflowNodeSchema = z.lazy(() =>
  nodeBaseSchema
    .extend({
      runtimeType: z.literal('Subflow'),
      workflowFilePath: z.string().default(''),
      workflowDefinition: workflowDefinitionSchema.nullable().optional(),
      inputMappings: z.record(z.string(), z.string()).default({}),
      outputMappings: z.record(z.string(), z.string()).default({}),
      timeout: z.string().default('00:05:00'),
      skipValidation: z.boolean().default(false),
    })
    .strict()
    .superRefine((val, ctx) => {
      const hasPath = (val.workflowFilePath ?? '') !== '';
      const hasInline = val.workflowDefinition != null;
      if (hasPath && hasInline) {
        ctx.addIssue({
          code: 'custom',
          message:
            'Exactly one of WorkflowFilePath or WorkflowDefinition must be set',
        });
      }
      if (!hasPath && !hasInline) {
        ctx.addIssue({
          code: 'custom',
          message:
            'Exactly one of WorkflowFilePath or WorkflowDefinition must be set',
        });
      }
    }),
) as unknown as z.ZodObject<z.ZodRawShape>;

// Container schema — uses z.lazy to break recursion
const containerNodeSchema = z.lazy(() =>
  nodeBaseSchema
    .extend({
      runtimeType: z.literal('Container'),
      childNodes: z.array(nodeDtoSchema).nullable().optional(),
      childConnections: z.array(nodeConnectionSchema).nullable().optional(),
      executionMode: z.enum(['Sequential', 'Parallel']).default('Sequential'),
    })
    .strict(),
) as unknown as z.ZodObject<z.ZodRawShape>;

export const nodeDtoSchema = z.discriminatedUnion('runtimeType', [
  noopNodeSchema,
  csharpNodeSchema,
  csharpScriptNodeSchemaBase,
  csharpTaskNodeSchema,
  powershellNodeSchema,
  powershellTaskNodeSchema,
  ifElseNodeSchema,
  forEachNodeSchema,
  whileNodeSchema,
  switchNodeSchema,
  timerNodeSchema,
  subflowNodeSchema,
  containerNodeSchema,
]);

export type NodeDto = z.infer<typeof nodeDtoSchema>;

export function handleNode<T>(
  n: { runtimeType: RuntimeType },
  handlers: { [K in RuntimeType]: (node: { runtimeType: K }) => T },
): T {
  const handler = handlers[n.runtimeType as RuntimeType];
  if (!handler) {
    throw new Error(`Unhandled runtime type: ${n.runtimeType}`);
  }
  return handler(n as never);
}

// Re-export kind schemas for direct use
export { subflowNodeSchema, containerNodeSchema };
