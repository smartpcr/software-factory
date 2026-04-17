import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

export const csharpScriptNodeSchemaBase = nodeBaseSchema
  .extend({
    runtimeType: z.literal('CSharpScript'),
    scriptPath: z.string().default(''),
    scriptContent: z.string().default(''),
  })
  .strict();

export const csharpScriptNodeSchema = csharpScriptNodeSchemaBase.superRefine(
  (val, ctx) => {
    const hasPath = (val.scriptPath ?? '') !== '';
    const hasContent = (val.scriptContent ?? '') !== '';
    if (hasPath && hasContent) {
      ctx.addIssue({
        code: 'custom',
        message:
          'Exactly one of ScriptPath or ScriptContent must be set, not both',
      });
    }
    if (!hasPath && !hasContent) {
      ctx.addIssue({
        code: 'custom',
        message: 'ScriptPath or ScriptContent must be set',
      });
    }
  },
);

export type CSharpScriptNodeZod = z.infer<typeof csharpScriptNodeSchemaBase>;
