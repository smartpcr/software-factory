import { useGraphStore } from '../state/graphStore';
import { useNodeForm } from './useNodeForm';
import { FieldRenderer } from './FieldRenderer';
import type { NodeDto } from '../../engine-contract';

export function PropertyGrid() {
  const nodes = useGraphStore((s) => s.nodes);
  const selectedNodeIds = useGraphStore((s) => s.selectedNodeIds);

  const firstSelectedId = Array.from(selectedNodeIds)[0] as string | undefined;
  const selectedNode: NodeDto | null =
    firstSelectedId
      ? (nodes.find((n) => n.nodeId === firstSelectedId) ?? null)
      : null;

  const { form, onSubmit, fieldDescriptors } = useNodeForm(selectedNode);

  if (!selectedNode) {
    return (
      <div data-testid="property-grid-empty">
        <p>No node selected</p>
      </div>
    );
  }

  return (
    <div data-testid="property-grid">
      <form
        data-testid="property-grid-form"
        onSubmit={form.handleSubmit(onSubmit)}
      >
        <h3 data-testid="property-grid-title">
          {selectedNode.runtimeType} Properties
        </h3>
        {fieldDescriptors.map((desc) => (
          <FieldRenderer
            key={desc.name}
            descriptor={desc}
            form={form}
          />
        ))}
        <button type="submit" data-testid="property-grid-submit">
          Apply
        </button>
      </form>
    </div>
  );
}
