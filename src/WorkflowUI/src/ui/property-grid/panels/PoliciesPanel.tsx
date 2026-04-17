import { useState } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import { RetryPolicyEditor } from './RetryPolicyEditor';
import { CircuitBreakerPolicyEditor } from './CircuitBreakerPolicyEditor';

export interface PoliciesPanelProps {
  form: UseFormReturn<Record<string, unknown>>;
}

export function PoliciesPanel({ form }: PoliciesPanelProps) {
  const retryPolicy = form.watch('retryPolicy') as Record<string, unknown> | null | undefined;
  const circuitBreakerPolicy = form.watch('circuitBreakerPolicy') as Record<string, unknown> | null | undefined;

  const [retryEnabled, setRetryEnabled] = useState(retryPolicy != null);
  const [cbEnabled, setCbEnabled] = useState(circuitBreakerPolicy != null);

  const handleRetryToggle = () => {
    const next = !retryEnabled;
    setRetryEnabled(next);
    if (!next) {
      form.setValue('retryPolicy', null);
    } else {
      form.setValue('retryPolicy', {
        strategy: 'None',
        maxAttempts: 3,
        initialDelay: '00:00:01',
        maxDelay: '00:01:00',
        multiplier: 2.0,
        retryOn: [],
        doNotRetryOn: [],
      });
    }
  };

  const handleCbToggle = () => {
    const next = !cbEnabled;
    setCbEnabled(next);
    if (!next) {
      form.setValue('circuitBreakerPolicy', null);
    } else {
      form.setValue('circuitBreakerPolicy', {
        failureThreshold: 50,
        halfOpenSuccesses: 3,
        minimumThroughput: 10,
        openDuration: '00:00:30',
      });
    }
  };

  return (
    <div data-testid="policies-panel">
      <div data-testid="field-joinType">
        <label htmlFor="joinType">Join Type</label>
        <select {...form.register('joinType')} id="joinType">
          <option value="Any">Any</option>
          <option value="All">All</option>
        </select>
      </div>

      <div data-testid="field-priority">
        <label htmlFor="priority">Priority</label>
        <select {...form.register('priority')} id="priority">
          <option value="Low">Low</option>
          <option value="Normal">Normal</option>
          <option value="High">High</option>
        </select>
      </div>

      <div data-testid="field-maxConcurrentExecutions">
        <label htmlFor="maxConcurrentExecutions">Max Concurrency</label>
        <input
          {...form.register('maxConcurrentExecutions', { valueAsNumber: true })}
          id="maxConcurrentExecutions"
          type="number"
        />
      </div>

      <div data-testid="field-retryPolicy">
        <label>
          <input
            type="checkbox"
            checked={retryEnabled}
            onChange={handleRetryToggle}
            data-testid="retry-toggle"
          />
          Retry Policy
        </label>
        {retryEnabled && <RetryPolicyEditor form={form} />}
      </div>

      <div data-testid="field-circuitBreakerPolicy">
        <label>
          <input
            type="checkbox"
            checked={cbEnabled}
            onChange={handleCbToggle}
            data-testid="cb-toggle"
          />
          Circuit Breaker Policy
        </label>
        {cbEnabled && <CircuitBreakerPolicyEditor form={form} />}
      </div>
    </div>
  );
}
