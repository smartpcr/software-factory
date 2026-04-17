import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const powershellNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('PowerShell'),
    scriptPath: z.string().default(''),
    requiredModules: z.array(z.string()).nullable().optional(),
    modulePaths: z.record(z.string(), z.string()).nullable().optional(),
  })
  .strict();

export type PowerShellNodeZod = z.infer<typeof powershellNodeSchema>;
