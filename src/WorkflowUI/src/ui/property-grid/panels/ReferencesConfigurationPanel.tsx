import type { UseFormReturn } from 'react-hook-form';

export interface ReferencesConfigurationPanelProps {
  form: UseFormReturn<Record<string, unknown>>;
}

export function ReferencesConfigurationPanel({ form }: ReferencesConfigurationPanelProps) {
  return (
    <div data-testid="references-panel">
      <div data-testid="field-compensationNodeId">
        <label htmlFor="compensationNodeId">Compensation Node ID</label>
        <input
          {...form.register('compensationNodeId')}
          id="compensationNodeId"
          type="text"
          placeholder="Node ID for compensation"
        />
      </div>

      <div data-testid="field-fallbackNodeId">
        <label htmlFor="fallbackNodeId">Fallback Node ID</label>
        <input
          {...form.register('fallbackNodeId')}
          id="fallbackNodeId"
          type="text"
          placeholder="Node ID for fallback"
        />
      </div>

      <div data-testid="field-configuration">
        <label htmlFor="configuration">Configuration</label>
        <textarea
          {...form.register('configuration')}
          id="configuration"
          placeholder="JSON configuration"
          rows={4}
        />
      </div>
    </div>
  );
}
