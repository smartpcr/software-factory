import { useCallback, useState } from 'react';
import { DRAG_TYPE, decodeDragPayload } from '../palette/dragPayload';
import './DropZone.css';

export interface DropZoneProps {
  nodeId: string;
  position: 'before' | 'after' | 'after-error';
  onDrop?: (runtimeType: string, nodeId: string, position: string) => void;
}

export function DropZone({ nodeId, position, onDrop }: DropZoneProps) {
  const [active, setActive] = useState(false);
  const className = [
    'drop-zone',
    position === 'before' ? 'above' : position === 'after-error' ? 'error-right' : 'below',
    active ? 'drop-active' : '',
  ].filter(Boolean).join(' ');

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
      onDrop(payload.runtimeType, nodeId, position);
    }
  }, [nodeId, position, onDrop]);

  return (
    <div
      className={className}
      data-node-id={nodeId}
      data-position={position}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    />
  );
}
