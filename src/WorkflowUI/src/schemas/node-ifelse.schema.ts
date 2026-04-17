import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const ifElseNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('IfElse'),
    condition: z.string().min(1),
  })
  .strict();

export type IfElseNodeZod = z.infer<typeof ifElseNodeSchema>;
