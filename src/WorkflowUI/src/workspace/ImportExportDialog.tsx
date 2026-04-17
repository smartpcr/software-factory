import { useRef, useState } from 'react';
import { exportWorkflows, importWorkflows } from '../api/files';

interface ImportExportDialogProps {
  workflowIds: string[];
  onImported?: () => void;
}

export function ImportExportDialog({
  workflowIds,
  onImported,
}: ImportExportDialogProps) {
  const [status, setStatus] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const handleExport = async () => {
    if (workflowIds.length === 0) {
      setStatus('No workflows selected for export');
      return;
    }
    try {
      const blob = await exportWorkflows(workflowIds);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'workflows.zip';
      a.click();
      URL.revokeObjectURL(url);
      setStatus(`Exported ${String(workflowIds.length)} workflows`);
    } catch (err) {
      setStatus(err instanceof Error ? err.message : 'Export failed');
    }
  };

  const handleImport = async () => {
    const file = fileRef.current?.files?.[0];
    if (!file) {
      setStatus('Please select a zip file');
      return;
    }
    try {
      const result = await importWorkflows(file);
      setStatus(`Imported ${String(result.imported.length)} workflows`);
      onImported?.();
    } catch (err) {
      setStatus(err instanceof Error ? err.message : 'Import failed');
    }
  };

  return (
    <div className="import-export-dialog" aria-label="Import/Export">
      <h3>Import / Export</h3>

      <div>
        <button onClick={handleExport} aria-label="Export workflows">
          Export Selected
        </button>
      </div>

      <div>
        <input
          ref={fileRef}
          type="file"
          accept=".zip"
          aria-label="Import zip file"
        />
        <button onClick={handleImport} aria-label="Import workflows">
          Import
        </button>
      </div>

      {status && (
        <div role="status" aria-live="polite">
          {status}
        </div>
      )}
    </div>
  );
}
