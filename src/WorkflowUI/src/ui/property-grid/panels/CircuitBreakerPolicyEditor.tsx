import type { UseFormReturn } from 'react-hook-form';

export interface CircuitBreakerPolicyEditorProps {
  form: UseFormReturn<Record<string, unknown>>;
}

export function CircuitBreakerPolicyEditor({ form }: CircuitBreakerPolicyEditorProps) {
  return (
    <div data-testid="circuit-breaker-editor">
      <div data-testid="cb-field-failureThreshold">
        <label htmlFor="circuitBreakerPolicy.failureThreshold">Failure Threshold</label>
        <input
          {...form.register('circuitBreakerPolicy.failureThreshold', { valueAsNumber: true })}
          id="circuitBreakerPolicy.failureThreshold"
          type="number"
        />
      </div>

      <div data-testid="cb-field-halfOpenSuccesses">
        <label htmlFor="circuitBreakerPolicy.halfOpenSuccesses">Half-Open Successes</label>
        <input
          {...form.register('circuitBreakerPolicy.halfOpenSuccesses', { valueAsNumber: true })}
          id="circuitBreakerPolicy.halfOpenSuccesses"
          type="number"
        />
      </div>

      <div data-testid="cb-field-minimumThroughput">
        <label htmlFor="circuitBreakerPolicy.minimumThroughput">Minimum Throughput</label>
        <input
          {...form.register('circuitBreakerPolicy.minimumThroughput', { valueAsNumber: true })}
          id="circuitBreakerPolicy.minimumThroughput"
          type="number"
        />
      </div>

      <div data-testid="cb-field-openDuration">
        <label htmlFor="circuitBreakerPolicy.openDuration">Open Duration</label>
        <input
          {...form.register('circuitBreakerPolicy.openDuration')}
          id="circuitBreakerPolicy.openDuration"
          type="text"
        />
      </div>
    </div>
  );
}
