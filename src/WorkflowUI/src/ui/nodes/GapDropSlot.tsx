import { useCallback, useState } from 'react';
import { DRAG_TYPE, decodeDragPayload } from '../palette/dragPayload';

export interface GapDropSlotProps {
  parentId: string;
  slot: string;
  index: number;
  onDrop?: (runtimeType: string, parentId: string, slot: string, index: number) => void;
}

export function GapDropSlot({ parentId, slot, index, onDrop }: GapDropSlotProps) {
  const [active, setActive] = useState(false);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(DRAG_TYPE)) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'copy';
    setActive(true);
  }, []);

  const handleDragLeave = useCallback(() => setActive(false), []);

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setActive(false);
    const payload = decodeDragPayload(e.dataTransfer.getData(DRAG_TYPE));
    if (payload && onDrop) {
      onDrop(payload.runtimeType, parentId, slot, index);
    }
  }, [parentId, slot, index, onDrop]);

  return (
    <div
      className={`gap-drop-slot${active ? ' drop-active' : ''}`}
      data-parent-id={parentId}
      data-slot={slot}
      data-index={index}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      Drop here
    </div>
  );
}
