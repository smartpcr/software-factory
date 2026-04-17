import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const noopNodeSchema = nodeBaseSchema
  .extend({ runtimeType: z.literal('Noop') })
  .strict();

export type NoopNodeZod = z.infer<typeof noopNodeSchema>;
