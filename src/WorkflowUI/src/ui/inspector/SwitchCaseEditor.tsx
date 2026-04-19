export interface SwitchCaseEditorProps {
  cases: string[];
  onChange: (cases: string[]) => void;
}

export function SwitchCaseEditor({ cases, onChange }: SwitchCaseEditorProps) {
  const handleRename = (index: number, value: string) => {
    const next = [...cases];
    next[index] = value;
    onChange(next);
  };

  const handleRemove = (index: number) => {
    onChange(cases.filter((_, i) => i !== index));
  };

  const handleAdd = () => {
    onChange([...cases, `case_${cases.length + 1}`]);
  };

  return (
    <div data-testid="switch-case-editor">
      <div className="case-list">
        {cases.map((c, i) => (
          <div key={i} className="case-row">
            <input
              value={c}
              onChange={(e) => handleRename(i, e.target.value)}
              className="mono"
              data-testid={`case-input-${i}`}
            />
            <button
              type="button"
              className="case-del-btn"
              onClick={() => handleRemove(i)}
              aria-label={`Remove case ${c}`}
              data-testid={`case-del-${i}`}
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </button>
          </div>
        ))}
      </div>
      <button type="button" className="add-case-btn" onClick={handleAdd} data-testid="add-case-btn">
        + Add case
      </button>
    </div>
  );
}
