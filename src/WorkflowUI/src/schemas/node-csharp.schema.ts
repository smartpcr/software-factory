import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const csharpNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('CSharp'),
    assemblyPath: z.string().nullable().optional(),
    typeName: z.string().nullable().optional(),
  })
  .strict();

export type CSharpNodeZod = z.infer<typeof csharpNodeSchema>;
