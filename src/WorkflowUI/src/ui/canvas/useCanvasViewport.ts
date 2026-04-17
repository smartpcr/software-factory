import { useCallback } from 'react';
import type { Viewport } from '@xyflow/react';
import { useGraphStore } from '../state/graphStore';

export function useCanvasViewport() {
  const setViewport = useGraphStore((s) => s.setViewport);
  const viewport = useGraphStore((s) => s.viewport);

  const onViewportChange = useCallback(
    (vp: Viewport) => {
      setViewport({ x: vp.x, y: vp.y, zoom: vp.zoom });
    },
    [setViewport],
  );

  return { viewport, onViewportChange };
}