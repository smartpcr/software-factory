import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useForm } from 'react-hook-form';
import { PoliciesPanel } from '../../property-grid/panels/PoliciesPanel';

function TestWrapper({ defaultValues }: { defaultValues: Record<string, unknown> }) {
  const form = useForm<Record<string, unknown>>({ defaultValues });
  return <PoliciesPanel form={form} />;
}

describe('PoliciesPanel', () => {
  it('renders 5 top-level controls', () => {
    render(
      <TestWrapper
        defaultValues={{
          joinType: 'Any',
          priority: 'Normal',
          maxConcurrentExecutions: 0,
          retryPolicy: null,
          circuitBreakerPolicy: null,
        }}
      />,
    );

    expect(screen.getByTestId('field-joinType')).toBeDefined();
    expect(screen.getByTestId('field-priority')).toBeDefined();
    expect(screen.getByTestId('field-maxConcurrentExecutions')).toBeDefined();
    expect(screen.getByTestId('field-retryPolicy')).toBeDefined();
    expect(screen.getByTestId('field-circuitBreakerPolicy')).toBeDefined();
  });

  it('RetryPolicy editor shows 7 fields when enabled', () => {
    render(
      <TestWrapper
        defaultValues={{
          joinType: 'Any',
          priority: 'Normal',
          maxConcurrentExecutions: 0,
          retryPolicy: {
            strategy: 'None',
            maxAttempts: 3,
            initialDelay: '00:00:01',
            maxDelay: '00:01:00',
            multiplier: 2.0,
            retryOn: [],
            doNotRetryOn: [],
          },
          circuitBreakerPolicy: null,
        }}
      />,
    );

    expect(screen.getByTestId('retry-policy-editor')).toBeDefined();
    expect(screen.getByTestId('retry-field-strategy')).toBeDefined();
    expect(screen.getByTestId('retry-field-maxAttempts')).toBeDefined();
    expect(screen.getByTestId('retry-field-initialDelay')).toBeDefined();
    expect(screen.getByTestId('retry-field-maxDelay')).toBeDefined();
    expect(screen.getByTestId('retry-field-multiplier')).toBeDefined();
    expect(screen.getByTestId('retry-field-retryOn')).toBeDefined();
    expect(screen.getByTestId('retry-field-doNotRetryOn')).toBeDefined();
  });

  it('CircuitBreakerPolicy editor shows 4 fields when enabled', () => {
    render(
      <TestWrapper
        defaultValues={{
          joinType: 'Any',
          priority: 'Normal',
          maxConcurrentExecutions: 0,
          retryPolicy: null,
          circuitBreakerPolicy: {
            failureThreshold: 50,
            halfOpenSuccesses: 3,
            minimumThroughput: 10,
            openDuration: '00:00:30',
          },
        }}
      />,
    );

    expect(screen.getByTestId('circuit-breaker-editor')).toBeDefined();
    expect(screen.getByTestId('cb-field-failureThreshold')).toBeDefined();
    expect(screen.getByTestId('cb-field-halfOpenSuccesses')).toBeDefined();
    expect(screen.getByTestId('cb-field-minimumThroughput')).toBeDefined();
    expect(screen.getByTestId('cb-field-openDuration')).toBeDefined();
  });

  it('null/disabled policies render toggle-off state', () => {
    render(
      <TestWrapper
        defaultValues={{
          joinType: 'Any',
          priority: 'Normal',
          maxConcurrentExecutions: 0,
          retryPolicy: null,
          circuitBreakerPolicy: null,
        }}
      />,
    );

    const retryToggle = screen.getByTestId('retry-toggle') as HTMLInputElement;
    const cbToggle = screen.getByTestId('cb-toggle') as HTMLInputElement;

    expect(retryToggle.checked).toBe(false);
    expect(cbToggle.checked).toBe(false);
    expect(screen.queryByTestId('retry-policy-editor')).toBeNull();
    expect(screen.queryByTestId('circuit-breaker-editor')).toBeNull();
  });
});
