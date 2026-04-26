import { describe, expect, it } from 'vitest';
import { checkDragPolicy } from './policy';
import { policyScenarios } from './policy.fixtures';

describe('checkDragPolicy parity', () => {
  for (const scenario of policyScenarios) {
    it(scenario.name, () => {
      const r = checkDragPolicy(scenario.fromCol, scenario.toCol, scenario.card);
      expect(r.allow).toBe(scenario.expected.allow);
      expect(r.confirm ?? false).toBe(scenario.expected.confirm ?? false);
      expect(r.title ?? null).toBe(scenario.expected.title ?? null);
      expect(r.text ?? null).toBe(scenario.expected.text ?? null);
      expect(r.warn ?? null).toBe(scenario.expected.warn ?? null);
      expect(r.toast ?? null).toBe(scenario.expected.toast ?? null);
    });
  }
});
