import { useGraphStore } from '../state/graphStore';
import { useNodeForm } from './useNodeForm';
import { FieldRenderer } from './FieldRenderer';
import { PoliciesPanel } from './panels/PoliciesPanel';
import { ReferencesConfigurationPanel } from './panels/ReferencesConfigurationPanel';
import type { NodeDto } from '../../engine-contract';

export function PropertyGrid() {
  const nodes = useGraphStore((s) => s.nodes);
  const selectedNodeIds = useGraphStore((s) => s.selectedNodeIds);

  const firstSelectedId = Array.from(selectedNodeIds)[0] as string | undefined;
  const selectedNode: NodeDto | null =
    firstSelectedId
      ? (nodes.find((n) => n.nodeId === firstSelectedId) ?? null)
      : null;

  const { form, onSubmit, fieldDescriptors, mappedErrors } = useNodeForm(selectedNode);

  if (!selectedNode) {
    return (
      <div data-testid="property-grid-empty">
        <p>No node selected</p>
      </div>
    );
  }

  const generalFields = fieldDescriptors.filter((d) => d.group === 'general');
  const kindFields = fieldDescriptors.filter((d) => d.group === 'kind-specific');

  return (
    <div data-testid="property-grid">
      <form
        data-testid="property-grid-form"
        onSubmit={form.handleSubmit(onSubmit)}
      >
        <h3 data-testid="property-grid-title">
          {selectedNode.runtimeType} Properties
        </h3>

        <fieldset data-testid="general-section">
          <legend>General</legend>
          {generalFields.map((desc) => (
            <FieldRenderer key={desc.name} descriptor={desc} form={form} mappedErrors={mappedErrors} />
          ))}
        </fieldset>

        {kindFields.length > 0 && (
          <fieldset data-testid="kind-specific-section">
            <legend>Type-Specific</legend>
            {kindFields.map((desc) => (
              <FieldRenderer key={desc.name} descriptor={desc} form={form} mappedErrors={mappedErrors} />
            ))}
          </fieldset>
        )}

        <fieldset data-testid="policies-section">
          <legend>Policies</legend>
          <PoliciesPanel form={form} />
        </fieldset>

        <fieldset data-testid="references-section">
          <legend>References &amp; Configuration</legend>
          <ReferencesConfigurationPanel form={form} />
        </fieldset>

        <button type="submit" data-testid="property-grid-submit">
          Apply
        </button>
      </form>
    </div>
  );
}
