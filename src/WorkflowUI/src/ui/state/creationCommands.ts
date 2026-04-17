import type { RuntimeType } from '../../engine-contract';
import { createNodeFromKind } from '../palette/createNodeFromKind';
import { useGraphStore, type SidecarNodeMeta } from './graphStore';

export function executeCreateNode(
  runtimeType: RuntimeType,
  position: { x: number; y: number },
): string {
  const node = createNodeFromKind(runtimeType);
  const meta: SidecarNodeMeta = { x: position.x, y: position.y };
  useGraphStore.getState().addNode(node, meta);
  return node.nodeId;
}