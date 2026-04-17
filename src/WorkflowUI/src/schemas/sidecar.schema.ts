import { z } from 'zod';

export const sidecarSchema = z
  .object({
    version: z.literal('1.0'),
    workflowFile: z.string().min(1),
    nodePositions: z
      .record(z.string(), z.object({ x: z.number(), y: z.number() }))
      .default({}),
    nodeColors: z
      .record(z.string(), z.string().regex(/^#[0-9a-fA-F]{6}$/))
      .default({}),
    nodeCollapsed: z.record(z.string(), z.boolean()).default({}),
    notes: z
      .array(
        z.object({
          id: z.string().min(1),
          x: z.number(),
          y: z.number(),
          text: z.string(),
          color: z
            .string()
            .regex(/^#[0-9a-fA-F]{6}$/)
            .optional(),
        }),
      )
      .default([]),
    edgeLabelOffsets: z
      .record(z.string(), z.object({ dx: z.number(), dy: z.number() }))
      .default({}),
  })
  .strict();

export type SidecarZod = z.infer<typeof sidecarSchema>;
