import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const forEachNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('ForEach'),
    collectionExpression: z.string().min(1),
    itemVariableName: z.string().min(1).default('item'),
  })
  .strict();

export type ForEachNodeZod = z.infer<typeof forEachNodeSchema>;
