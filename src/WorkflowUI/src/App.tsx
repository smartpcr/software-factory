import { useState } from 'react';
import { WorkflowCanvas } from './ui/canvas/WorkflowCanvas';
import { Palette } from './ui/palette/Palette';
import { ValidationPanel } from './ui/validation/ValidationPanel';
import { useWorkflowValidation } from './ui/validation/useWorkflowValidation';
import { focusValidationTarget } from './ui/validation/focusValidationTarget';
import { WorkspaceTree } from './workspace/WorkspaceTree';
import { ImportExportDialog } from './workspace/ImportExportDialog';
import { DuplicateDialog } from './workspace/DuplicateDialog';
import { AppShell } from './ui/layout/AppShell';
import { Topbar } from './ui/topbar/Topbar';
import { Inspector } from './ui/inspector/Inspector';
import { useGraphStore } from './ui/state/graphStore';

export function App() {
  const { entries, validate } = useWorkflowValidation();
  const [selectedWorkflowId, setSelectedWorkflowId] = useState<string | null>(null);
  const [showImportExport, setShowImportExport] = useState(false);
  const [duplicateSourceId, setDuplicateSourceId] = useState<string | null>(null);
  const workflowName = useGraphStore((s) => s.workflowName);

  const topbarContent = (
    <Topbar
      workflowName={workflowName}
      onWorkflowNameChange={() => {}}
      onImport={() => setShowImportExport(true)}
    />
  );

  const paletteContent = (
    <>
      <WorkspaceTree onSelect={setSelectedWorkflowId} />
      <div style={{ padding: 8, borderTop: '1px solid var(--stroke-1)' }}>
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
      <Palette />
    </>
  );

  const canvasContent = (
    <div style={{ position: 'relative', width: '100%', height: '100%' }}>
      <WorkflowCanvas showMiniMap showControls />
    </div>
  );

  const inspectorContent = (
    <>
      <Inspector />
      <div data-testid="validation-bar" style={{ borderTop: '1px solid var(--stroke-1)', maxHeight: 200, overflow: 'auto' }}>
        <button type="button" onClick={validate} data-testid="validate-workflow-btn" style={{ margin: 4 }}>
          Validate
        </button>
        <ValidationPanel entries={entries} onJumpToNode={focusValidationTarget} />
      </div>
    </>
  );

  return (
    <AppShell
      topbar={topbarContent}
      palette={paletteContent}
      canvas={canvasContent}
      inspector={inspectorContent}
      logs={<div style={{ padding: 8, color: 'var(--text-3)', fontSize: 11 }}>Execution log — coming soon</div>}
    />
  );
}
