import { useState } from 'react';
import { duplicateWorkflow } from '../api/files';

interface DuplicateDialogProps {
  sourceId: string;
  onDuplicated?: (newId: string) => void;
}

export function DuplicateDialog({
  sourceId,
  onDuplicated,
}: DuplicateDialogProps) {
  const [targetId, setTargetId] = useState('');
  const [status, setStatus] = useState<string | null>(null);

  const handleDuplicate = async () => {
    try {
      const result = await duplicateWorkflow(
        sourceId,
        targetId.trim() || undefined,
      );
      setStatus(`Duplicated as ${result.id}`);
      onDuplicated?.(result.id);
    } catch (err) {
      setStatus(err instanceof Error ? err.message : 'Duplicate failed');
    }
  };

  return (
    <div className="duplicate-dialog" aria-label="Duplicate workflow">
      <h3>Duplicate Workflow</h3>
      <p>Source: {sourceId}</p>

      <label>
        Target ID (optional):
        <input
          aria-label="Target workflow ID"
          placeholder="Leave blank for auto-generated"
          value={targetId}
          onChange={(e) => setTargetId(e.target.value)}
        />
      </label>

      <button onClick={handleDuplicate} aria-label="Duplicate">
        Duplicate
      </button>

      {status && (
        <div role="status" aria-live="polite">
          {status}
        </div>
      )}
    </div>
  );
}
