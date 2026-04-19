export interface BranchEditorProps {
  branches: string[];
  onChange: (branches: string[]) => void;
}

export function BranchEditor({ branches, onChange }: BranchEditorProps) {
  const handleRename = (index: number, value: string) => {
    const next = [...branches];
    next[index] = value;
    onChange(next);
  };

  const handleRemove = (index: number) => {
    onChange(branches.filter((_, i) => i !== index));
  };

  const handleAdd = () => {
    onChange([...branches, `branch_${branches.length + 1}`]);
  };

  return (
    <div data-testid="branch-editor">
      <div className="case-list">
        {branches.map((b, i) => (
          <div key={i} className="case-row">
            <input
              value={b}
              onChange={(e) => handleRename(i, e.target.value)}
              className="mono"
              data-testid={`branch-input-${i}`}
            />
            <button
              type="button"
              className="case-del-btn"
              onClick={() => handleRemove(i)}
              aria-label={`Remove branch ${b}`}
              data-testid={`branch-del-${i}`}
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </button>
          </div>
        ))}
      </div>
      <button type="button" className="add-case-btn" onClick={handleAdd} data-testid="add-branch-btn">
        + Add branch
      </button>
    </div>
  );
}
