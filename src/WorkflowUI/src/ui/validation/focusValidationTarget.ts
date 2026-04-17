import { useGraphStore } from '../state/graphStore';

/**
 * Updates selectedNodeIds to the target node and centers viewport on it.
 */
export function focusValidationTarget(nodeId: string): void {
  const state = useGraphStore.getState();

  // Select the target node
  state.setSelectedNodeIds(new Set([nodeId]));

  // Center viewport on the target node's position
  const meta = state.sidecarMeta.get(nodeId);
  if (meta) {
    state.setViewport({
      x: -meta.x + 400,
      y: -meta.y + 300,
      zoom: state.viewport.zoom,
    });
  }
}
