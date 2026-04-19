import { useTheme } from '../theme/useTheme';
import { WorkflowTitleInput } from './WorkflowTitleInput';
import { ExecutionIndicator, type ExecutionState } from './ExecutionIndicator';
import { RunControls } from './RunControls';
import './Topbar.css';

export interface TopbarProps {
  workflowName: string;
  onWorkflowNameChange: (name: string) => void;
  isDirty?: boolean;
  isSaved?: boolean;
  executionState?: ExecutionState;
  onNew?: () => void;
  onSave?: () => void;
  onImport?: () => void;
  onExport?: () => void;
  onDelete?: () => void;
  onRun?: () => void;
  onPause?: () => void;
  onResume?: () => void;
  onRerun?: () => void;
  onCancel?: () => void;
}

export function Topbar({
  workflowName,
  onWorkflowNameChange,
  isDirty,
  isSaved,
  executionState = 'idle',
  onNew,
  onSave,
  onImport,
  onExport,
  onDelete,
  onRun,
  onPause,
  onResume,
  onRerun,
  onCancel,
}: TopbarProps) {
  const { theme, toggleTheme } = useTheme();

  return (
    <div className="topbar" data-testid="topbar">
      {/* Brand */}
      <div className="brand">
        <div className="brand-mark" />
        <div>
          <div className="brand-name">Flow Studio</div>
          <div className="brand-meta mono">workflow designer</div>
        </div>
      </div>

      {/* Workflow title */}
      <WorkflowTitleInput
        value={workflowName}
        onChange={onWorkflowNameChange}
        isDirty={isDirty}
        isSaved={isSaved}
      />

      {/* File operations */}
      <div className="tb-group" data-testid="file-toolbar">
        <button className="tb-btn" onClick={onNew} title="New workflow" data-testid="btn-new">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>
          New
        </button>
        <button className="tb-btn" onClick={onSave} title="Save (Ctrl+S)" data-testid="btn-save">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z" />
            <path d="M17 21v-8H7v8M7 3v5h8" />
          </svg>
          Save
        </button>
        <button className="tb-btn" onClick={onImport} title="Import" data-testid="btn-import">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3" />
          </svg>
          Import
        </button>
        <button className="tb-btn" onClick={onExport} title="Export" data-testid="btn-export">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12" />
          </svg>
          Export
        </button>
        <button className="tb-btn danger" onClick={onDelete} title="Delete this workflow" data-testid="btn-delete">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
          </svg>
          Delete
        </button>
      </div>

      <div className="spacer" />

      {/* Theme toggle */}
      <button
        className="tb-btn"
        onClick={toggleTheme}
        aria-label="Toggle light / dark theme"
        title="Toggle light / dark theme"
        style={{ padding: '6px 10px', border: '1px solid var(--stroke-1)', borderRadius: 8, background: 'var(--bg-1)' }}
        data-testid="theme-toggle"
      >
        {theme === 'dark' ? (
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" width="14" height="14">
            <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
          </svg>
        ) : (
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" width="14" height="14">
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41" />
          </svg>
        )}
      </button>

      {/* Execution indicator */}
      <ExecutionIndicator state={executionState} />

      {/* Run controls */}
      <RunControls
        state={executionState}
        onRun={onRun}
        onPause={onPause}
        onResume={onResume}
        onRerun={onRerun}
        onCancel={onCancel}
      />
    </div>
  );
}
