import { describe, it, expect } from 'vitest';
import { PANEL_COUNTS } from '../../property-grid/panels/panelCountMetadata';
import { baseFieldDescriptors } from '../../property-grid/fieldDescriptors';

describe('partitionCounts', () => {
  it('general panel has exactly 3 fields', () => {
    const generalFields = baseFieldDescriptors().filter((d) => d.group === 'general');
    expect(generalFields.length).toBe(PANEL_COUNTS.general);
  });

  it('policies panel has exactly 5 top-level fields', () => {
    // joinType, priority, maxConcurrentExecutions from baseFieldDescriptors
    // plus retryPolicy and circuitBreakerPolicy (toggles in PoliciesPanel)
    const policyFields = baseFieldDescriptors().filter((d) => d.group === 'policies');
    // 3 from baseFieldDescriptors (joinType, priority, maxConcurrentExecutions)
    // + 2 toggles (retryPolicy, circuitBreakerPolicy) in PoliciesPanel
    expect(policyFields.length + 2).toBe(PANEL_COUNTS.policies);
  });

  it('references panel has exactly 3 fields', () => {
    expect(PANEL_COUNTS.references).toBe(3);
  });

  it('retry nested editor has exactly 7 fields', () => {
    expect(PANEL_COUNTS.retryNested).toBe(7);
  });

  it('circuitBreaker nested editor has exactly 4 fields', () => {
    expect(PANEL_COUNTS.circuitBreakerNested).toBe(4);
  });
});
