import { z } from 'zod';

export const retryStrategySchema = z.enum([
  'None',
  'Fixed',
  'Exponential',
  'Linear',
]);
export type RetryStrategy = z.infer<typeof retryStrategySchema>;

export const retryPolicySchema = z
  .object({
    strategy: retryStrategySchema.default('None'),
    maxAttempts: z.number().int().min(1).max(10).default(3),
    initialDelay: z.string().default('00:00:01'),
    maxDelay: z.string().default('00:01:00'),
    multiplier: z.number().default(2.0),
    retryOn: z.array(z.string()).default([]),
    doNotRetryOn: z.array(z.string()).default([]),
  })
  .strict();
export type RetryPolicyZod = z.infer<typeof retryPolicySchema>;

export const circuitBreakerPolicySchema = z
  .object({
    failureThreshold: z.number().int().min(0).max(100).default(50),
    minimumThroughput: z.number().int().min(1).default(10),
    openDuration: z.string().default('00:00:30'),
    halfOpenSuccesses: z.number().int().min(1).default(3),
  })
  .strict();
export type CircuitBreakerPolicyZod = z.infer<
  typeof circuitBreakerPolicySchema
>;
