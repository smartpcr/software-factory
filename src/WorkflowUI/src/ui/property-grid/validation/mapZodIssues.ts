import type { z } from 'zod';

export interface FieldError {
  path: string;
  message: string;
}

/**
 * Maps Zod validation issues to a flat list of field errors.
 */
export function mapZodIssues(issues: z.ZodIssue[]): FieldError[] {
  return issues.map((issue) => ({
    path: issue.path.map(String).join('.'),
    message: issue.message,
  }));
}
