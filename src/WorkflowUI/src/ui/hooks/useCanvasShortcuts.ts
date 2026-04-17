import { useEffect, useCallback, useState } from 'react';
import type { RuntimeType } from '../../engine-contract';
import { executeCreateNode } from '../state/creationCommands';

export function useCanvasShortcuts(canvasRef: React.RefObject<HTMLElement | null>) {
  const [showKindPicker, setShowKindPicker] = useState(false);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === 'n' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault();
        setShowKindPicker(true);
      }

      if (e.key === 'Delete' || e.key === 'Backspace') {
        // Delete selected nodes - handled by React Flow
      }
    },
    [],
  );

  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;

    el.addEventListener('keydown', handleKeyDown);
    return () => el.removeEventListener('keydown', handleKeyDown);
  }, [canvasRef, handleKeyDown]);

  const handleKindSelect = useCallback((runtimeType: RuntimeType) => {
    executeCreateNode(runtimeType, { x: 200, y: 200 });
    setShowKindPicker(false);
  }, []);

  return { showKindPicker, setShowKindPicker, handleKindSelect };
}