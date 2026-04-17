import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const csharpTaskNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('CSharpTask'),
    scriptContent: z.string().nullable().optional(),
    executorTypeName: z.string().nullable().optional(),
    executorAssemblyPath: z.string().nullable().optional(),
  })
  .strict();

export type CSharpTaskNodeZod = z.infer<typeof csharpTaskNodeSchema>;
