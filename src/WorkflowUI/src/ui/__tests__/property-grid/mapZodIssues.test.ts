import { describe, it, expect } from 'vitest';
import { mapZodIssues } from '../../property-grid/validation/mapZodIssues';
import type { z } from 'zod';

describe('mapZodIssues', () => {
  it('maps single issue to FieldError', () => {
    const issues: z.ZodIssue[] = [
      {
        code: 'too_small',
        minimum: 1,
        type: 'string',
        inclusive: true,
        exact: false,
        message: 'String must contain at least 1 character(s)',
        path: ['nodeName'],
      },
    ];

    const errors = mapZodIssues(issues);
    expect(errors).toHaveLength(1);
    expect(errors[0].path).toBe('nodeName');
    expect(errors[0].message).toBe('String must contain at least 1 character(s)');
  });

  it('maps nested path correctly', () => {
    const issues: z.ZodIssue[] = [
      {
        code: 'invalid_type',
        expected: 'number',
        received: 'string',
        message: 'Expected number, received string',
        path: ['retryPolicy', 'maxAttempts'],
      },
    ];

    const errors = mapZodIssues(issues);
    expect(errors).toHaveLength(1);
    expect(errors[0].path).toBe('retryPolicy.maxAttempts');
  });

  it('returns empty array for no issues', () => {
    const errors = mapZodIssues([]);
    expect(errors).toHaveLength(0);
  });
});
