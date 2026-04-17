import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const whileNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('While'),
    conditionExpression: z.string().min(1),
    maxIterations: z.number().int().min(1).max(1_000_000).default(1000),
  })
  .strict();

export type WhileNodeZod = z.infer<typeof whileNodeSchema>;
