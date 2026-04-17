import { z } from 'zod';
import {
  retryPolicySchema,
  circuitBreakerPolicySchema,
} from './policy.schema';

export const runtimeTypeSchema = z.enum([
  'Noop',
  'CSharp',
  'CSharpScript',
  'CSharpTask',
  'PowerShell',
  'PowerShellTask',
  'IfElse',
  'ForEach',
  'While',
  'Switch',
  'Subflow',
  'Timer',
  'Container',
]);
export type RuntimeTypeZod = z.infer<typeof runtimeTypeSchema>;

export const joinTypeSchema = z.enum(['Any', 'All']);
export const nodePrioritySchema = z.enum(['Low', 'Normal', 'High']);
export const messageTypeSchema = z.enum([
  'Complete',
  'Fail',
  'Progress',
  'Next',
  'Cancel',
]);

export const nodeBaseSchema = z.object({
  runtimeType: runtimeTypeSchema,
  nodeId: z.string().min(1),
  nodeName: z.string().default(''),
  description: z.string().nullable().optional(),
  configuration: z.record(z.string(), z.unknown()).nullable().optional(),
  joinType: joinTypeSchema.default('Any'),
  priority: nodePrioritySchema.default('Normal'),
  maxConcurrentExecutions: z.number().int().min(0).max(100).default(0),
  retryPolicy: retryPolicySchema.nullable().optional(),
  circuitBreakerPolicy: circuitBreakerPolicySchema.nullable().optional(),
  compensationNodeId: z.string().nullable().optional(),
  fallbackNodeId: z.string().nullable().optional(),
});

export type NodeDtoBase = z.infer<typeof nodeBaseSchema>;
