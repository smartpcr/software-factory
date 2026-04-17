import type { UseFormReturn } from 'react-hook-form';

export interface RetryPolicyEditorProps {
  form: UseFormReturn<Record<string, unknown>>;
}

export function RetryPolicyEditor({ form }: RetryPolicyEditorProps) {
  return (
    <div data-testid="retry-policy-editor">
      <div data-testid="retry-field-strategy">
        <label htmlFor="retryPolicy.strategy">Strategy</label>
        <select
          {...form.register('retryPolicy.strategy')}
          id="retryPolicy.strategy"
        >
          <option value="None">None</option>
          <option value="Fixed">Fixed</option>
          <option value="Exponential">Exponential</option>
          <option value="Linear">Linear</option>
        </select>
      </div>

      <div data-testid="retry-field-maxAttempts">
        <label htmlFor="retryPolicy.maxAttempts">Max Attempts</label>
        <input
          {...form.register('retryPolicy.maxAttempts', { valueAsNumber: true })}
          id="retryPolicy.maxAttempts"
          type="number"
        />
      </div>

      <div data-testid="retry-field-initialDelay">
        <label htmlFor="retryPolicy.initialDelay">Initial Delay</label>
        <input
          {...form.register('retryPolicy.initialDelay')}
          id="retryPolicy.initialDelay"
          type="text"
        />
      </div>

      <div data-testid="retry-field-maxDelay">
        <label htmlFor="retryPolicy.maxDelay">Max Delay</label>
        <input
          {...form.register('retryPolicy.maxDelay')}
          id="retryPolicy.maxDelay"
          type="text"
        />
      </div>

      <div data-testid="retry-field-multiplier">
        <label htmlFor="retryPolicy.multiplier">Multiplier</label>
        <input
          {...form.register('retryPolicy.multiplier', { valueAsNumber: true })}
          id="retryPolicy.multiplier"
          type="number"
          step="0.1"
        />
      </div>

      <div data-testid="retry-field-retryOn">
        <label htmlFor="retryPolicy.retryOn">Retry On</label>
        <input
          {...form.register('retryPolicy.retryOn')}
          id="retryPolicy.retryOn"
          type="text"
          placeholder="Comma-separated exception types"
        />
      </div>

      <div data-testid="retry-field-doNotRetryOn">
        <label htmlFor="retryPolicy.doNotRetryOn">Do Not Retry On</label>
        <input
          {...form.register('retryPolicy.doNotRetryOn')}
          id="retryPolicy.doNotRetryOn"
          type="text"
          placeholder="Comma-separated exception types"
        />
      </div>
    </div>
  );
}
