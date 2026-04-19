export interface WorkflowTitleInputProps{
  value: string;
  onChange: (value: string) => void;
  isDirty?: boolean;
  isSaved?: boolean;
}

export function WorkflowTitleInput({ value, onChange, isDirty, isSaved }: WorkflowTitleInputProps) {
  const dotClass = isDirty ? 'dirty' : isSaved ? 'saved' : '';

  return (
    <div className="wf-title" data-testid="wf-title">
      <input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Untitled workflow"
        aria-label="Workflow title"
        data-testid="wf-title-input"
      />
      <span
        className={`wf-status-dot ${dotClass}`}
        data-testid="wf-status-dot"
        title={isDirty ? 'Unsaved changes' : isSaved ? 'Saved' : 'No changes'}
      />
    </div>
  );
}
