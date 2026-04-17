import { useState } from 'react';
import { WorkflowCanvas } from './ui/canvas/WorkflowCanvas';
import { Palette } from './ui/palette/Palette';
import { PropertyGrid } from './ui/property-grid/PropertyGrid';
import { ValidationPanel } from './ui/validation/ValidationPanel';
import { useWorkflowValidation } from './ui/validation/useWorkflowValidation';
import { focusValidationTarget } from './ui/validation/focusValidationTarget';
import { WorkspaceTree } from './workspace/WorkspaceTree';
import { ImportExportDialog } from './workspace/ImportExportDialog';
import { DuplicateDialog } from './workspace/DuplicateDialog';

export function App() {
  const { entries, validate } = useWorkflowValidation();
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string | null>(null);
  const [showImportExport, setShowImportExport] = useState(false);
  const [duplicateSourceId, setDuplicateSourceId] = useState<string | null>(null);

  return (
    <div style={{ display: 'flex', height: '100vh' }} data-testid="app-root">
      <div style={{ width: 240, borderRight: '1px solid #e5e7eb', overflow: 'auto', display: 'flex', flexDirection: 'column' }} data-testid="workspace-sidebar">
        <WorkspaceTree onSelect={setSelectedWorkflowId} />
        <div style={{ padding: 8, borderTop: '1px solid #e5e7eb' }}>
          <button type="button" onClick={() => setShowImportExport(!showImportExport)} aria-label="Toggle import/export">
            Import/Export
          </button>
          {selectedWorkflowId && (
            <button type="button" onClick={() => setDuplicateSourceId(selectedWorkflowId)} aria-label="Duplicate selected">
              Duplicate
            </button>
          )}
        </div>
        {showImportExport && (
          <ImportExportDialog
            workflowIds={selectedWorkflowId ? [selectedWorkflowId] : []}
            onImported={() => setShowImportExport(false)}
          />
        )}
        {duplicateSourceId && (
          <DuplicateDialog
            sourceId={duplicateSourceId}
            onDuplicated={() => setDuplicateSourceId(null)}
          />
        )}
      </div>
      <Palette />
      <div style={{ flex: 1, position: 'relative', display: 'flex', flexDirection: 'column' }}>
        <div style={{ position: 'relative', flex: 1 }}>
          <h1 style={{ position: 'absolute', top: 8, left: 16, zIndex: 5, margin: 0, fontSize: 18 }}>
            Workflow UI
          </h1>
          <WorkflowCanvas showMiniMap showControls />
        </div>
        <div data-testid="validation-bar" style={{ borderTop: '1px solid #e5e7eb', maxHeight: 200, overflow: 'auto' }}>
          <button type="button" onClick={validate} data-testid="validate-workflow-btn" style={{ margin: 4 }}>
            Validate
          </button>
          <ValidationPanel entries={entries} onJumpToNode={focusValidationTarget} />
        </div>
      </div>
      <div style={{ width: 320, borderLeft: '1px solid #e5e7eb', overflow: 'auto' }} data-testid="property-grid-sidebar">
        <PropertyGrid />
      </div>
    </div>
  );
}
