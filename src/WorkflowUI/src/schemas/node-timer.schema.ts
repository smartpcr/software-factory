import { z } from 'zod';
import { nodeBaseSchema } from './node-base.schema';

const CRON_REGEX = /^(\S+\s+){4,5}\S+$/;
const DURATION_REGEX =
  /^P(?=[^T])?(\d+Y)?(\d+M)?(\d+D)?(T(\d+H)?(\d+M)?(\d+S)?)?$|^\d{2}:\d{2}:\d{2}$/;

export const timerNodeSchema = nodeBaseSchema
  .extend({
    runtimeType: z.literal('Timer'),
    schedule: z
      .string()
      .min(1)
      .refine((v) => CRON_REGEX.test(v) || DURATION_REGEX.test(v), {
        message: 'schedule must be CRON or ISO-8601/hh:mm:ss duration',
      }),
  })
  .strict();

export type TimerNodeZod = z.infer<typeof timerNodeSchema>;
