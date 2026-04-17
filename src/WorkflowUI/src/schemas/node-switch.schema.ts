import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const switchNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('Switch'),
    expression: z.string().min(1),
    cases: z.record(z.string(), z.string()).default({}),
  })
  .strict();

export type SwitchNodeZod = z.infer<typeof switchNodeSchema>;
