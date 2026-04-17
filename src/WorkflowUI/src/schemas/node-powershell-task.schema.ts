import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const powershellTaskNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('PowerShellTask'),
    scriptPath: z.string().nullable().optional(),
    scriptContent: z.string().nullable().optional(),
    requiredModules: z.array(z.string()).nullable().optional(),
    modulePaths: z.array(z.string()).nullable().optional(),
  })
  .strict();

export type PowerShellTaskNodeZod = z.infer<typeof powershellTaskNodeSchema>;
