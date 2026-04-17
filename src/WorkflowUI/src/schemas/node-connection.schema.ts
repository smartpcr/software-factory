import { z } from 'zod';
import { messageTypeSchema } from './node-base.schema';

export const nodeConnectionSchema = z
  .object({
    sourceNodeId: z.string().min(1),
    targetNodeId: z.string().min(1),
    triggerMessageType: messageTypeSchema.default('Complete'),
    condition: z.string().nullable().optional(),
    isEnabled: z.boolean().default(true),
    metadata: z.record(z.string(), z.string()).nullable().optional(),
    priority: z.number().int().default(0),
    sourcePort: z.string().nullable().optional(),
    targetPort: z.string().nullable().optional(),
  })
  .strict();

export type NodeConnectionZod = z.infer<typeof nodeConnectionSchema>;
