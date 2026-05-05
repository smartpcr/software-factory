import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { useProductsStore } from '@/state/useProductsStore';

const ACCEPT = '.md,.txt,.json,.csv,.yaml,.yml,.toml,.xml';
const ACCEPT_SET = new Set(['md','txt','json','csv','yaml','yml','toml','xml']);

interface Props {
  open: boolean;
  onClose: () => void;
}

// Product-scoped story creation modal. Creates under the active product.
// Description doubles as the prompt the architect agent receives when the
// story is picked up; points size the work for sprint planning.
export function NewStoryModal({ open, onClose }: Props) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [points, setPoints] = useState<string>('5');
  // Phase 45 — operator's default base branch for spawns produced from
  // this story. Empty input → "main".
  const [baseBranch, setBaseBranch] = useState<string>('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [files, setFiles] = useState<File[]>([]);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const activeProductId = useProductsStore((s) => s.activeProductId);

  useEffect(() => {
    if (!open) return;
    setName('');
    setDescription('');
    setPoints('5');
    setBaseBranch('');
    setBusy(false);
    setErr(null);
    setFiles([]);
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanName = name.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    if (!activeProductId) { setErr('No active product selected'); return; }
    const ptsNum = Number.parseInt(points, 10);
    if (Number.isNaN(ptsNum) || ptsNum < 0) { setErr('Points must be a non-negative integer'); return; }
    setBusy(true);
    setErr(null);
    try {
      // Phase 43 — upload files first, then pass uploadIds to create.
      let uploadIds: string[] | undefined;
      if (files.length > 0) {
        const result = await forgeApi.uploadStoryAttachments(files);
        uploadIds = [result.uploadId];
      }
      const trimmedBase = baseBranch.trim();
      const result = await forgeApi.createProductStory(activeProductId, {
        name: cleanName,
        description: description.trim(),
        points: ptsNum,
        uploadIds,
        // Phase 45 — empty / unset means the server uses the default ("main").
        baseBranch: trimmedBase.length > 0 ? trimmedBase : undefined,
      });
      useToastStore.getState().show(`Story "${result.story.name}" created`, 'success');
      if (result.branchWarnings && result.branchWarnings.length > 0) {
        useToastStore.getState().show(
          `Base branch '${trimmedBase}' could not be created in some repos: ${result.branchWarnings.join('; ')}`,
          'danger',
        );
      } else if (trimmedBase.length > 0 && trimmedBase !== 'main') {
        useToastStore.getState().show(`Base branch '${trimmedBase}' created in product repos`, 'success');
      }
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Create failed';
      setErr(msg);
    } finally {
      setBusy(false);
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    // Cmd/Ctrl+Enter to submit (plain Enter is consumed by the textarea).
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && !busy) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={onClose}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-story-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 520 }}
      >
        <h3 className="modal-title" id="new-story-title" style={{ margin: '0 0 12px' }}>
          New story
        </h3>
        <p className="modal-text" style={{ margin: '0 0 14px', fontSize: 12, color: 'var(--text-tertiary)' }}>
          The description is used as the user prompt when the architect agent picks this
          story up to start design. Points size the work for sprint planning.
        </p>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Name</span>
            <input
              ref={nameRef}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Multi-tenant authentication"
              maxLength={120}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
            />
          </label>

          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Description</span>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Describe the goal, constraints, and any context the architect should know."
              rows={6}
              disabled={busy}
              style={textareaStyle}
            />
            <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
              Used as the architect agent's user prompt.
            </span>
          </label>

          <label style={{ display: 'grid', gap: 5, maxWidth: 140 }}>
            <span style={labelStyle}>Points</span>
            <input
              type="number"
              min={0}
              step={1}
              value={points}
              onChange={(e) => setPoints(e.target.value)}
              disabled={busy}
              style={inputStyle}
            />
          </label>

          <label style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Base branch (optional, default: main)</span>
            <input
              value={baseBranch}
              onChange={(e) => setBaseBranch(e.target.value)}
              placeholder="main"
              disabled={busy}
              spellCheck={false}
              style={{ ...inputStyle, fontFamily: 'var(--font-mono)' }}
            />
            <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
              Default base branch for spawns produced from this story.
              Non-default values are created from <code>main</code> immediately
              in every repo of the product.
            </span>
          </label>

          <div style={{ display: 'grid', gap: 5 }}>
            <span style={labelStyle}>Reference documents (optional)</span>
            <FileDropZone files={files} onChange={setFiles} disabled={busy} />
          </div>

          {err && (
            <div role="alert" style={errStyle}>
              <span style={{ marginRight: 6 }}>⚠</span>
              {err}
            </div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="btn btn-primary"
            onClick={() => void submit()}
            disabled={busy || !name.trim()}
          >
            {busy ? 'Creating…' : 'Create story'}
          </button>
        </div>
      </div>
    </>
  );
}

const labelStyle: React.CSSProperties = {
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: 0.2,
  textTransform: 'uppercase',
  color: 'var(--text-secondary)',
};

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 11px',
  fontSize: 13,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const textareaStyle: React.CSSProperties = {
  ...inputStyle,
  resize: 'vertical',
  fontFamily: 'inherit',
  lineHeight: 1.45,
};

const errStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};

function fileIcon(name: string) {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  switch (ext) {
    case 'json': return '{ }';
    case 'csv':  return '⊞';
    case 'yaml': case 'yml': return '⁞';
    case 'xml':  return '⟨/⟩';
    default:     return '¶';
  }
}

function FileDropZone({ files, onChange, disabled }: { files: File[]; onChange: (f: File[]) => void; disabled: boolean }) {
  const [dragOver, setDragOver] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);

  function addFiles(incoming: File[]) {
    const valid = incoming.filter(f => {
      const ext = f.name.split('.').pop()?.toLowerCase() ?? '';
      return ACCEPT_SET.has(ext);
    });
    if (valid.length < incoming.length) {
      useToastStore.getState().show('Some files skipped — unsupported type', 'danger');
    }
    if (valid.length > 0) onChange([...files, ...valid]);
  }

  function remove(index: number) {
    onChange(files.filter((_, i) => i !== index));
  }

  return (
    <div style={{ display: 'grid', gap: 4 }}>
      {files.length > 0 && (
        <div style={{ display: 'grid', gap: 2 }}>
          {files.map((f, i) => (
            <div
              key={`${f.name}-${i}`}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '5px 8px',
                background: 'var(--bg-surface)',
                border: '1px solid var(--border)',
                borderRadius: 'var(--radius-sm)',
              }}
            >
              <span style={{
                width: 20, height: 20,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                borderRadius: 'var(--radius-sm)',
                background: 'var(--accent-bg)',
                color: 'var(--accent)',
                fontSize: 8,
                fontFamily: 'var(--font-mono)',
                fontWeight: 700,
                flexShrink: 0,
              }}>
                {fileIcon(f.name)}
              </span>
              <span style={{
                flex: 1, minWidth: 0,
                fontSize: 11,
                color: 'var(--text-primary)',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}>
                {f.name}
              </span>
              <span style={{
                fontSize: 9,
                fontFamily: 'var(--font-mono)',
                color: 'var(--text-tertiary)',
                flexShrink: 0,
              }}>
                {f.size < 1024 ? `${f.size} B` : `${(f.size / 1024).toFixed(1)} KB`}
              </span>
              <button
                type="button"
                title="Remove"
                onClick={() => remove(i)}
                disabled={disabled}
                style={{
                  width: 18, height: 18,
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  background: 'transparent',
                  border: '1px solid transparent',
                  borderRadius: 'var(--radius-sm)',
                  color: 'var(--text-tertiary)',
                  cursor: 'pointer',
                  fontSize: 11,
                  padding: 0,
                  flexShrink: 0,
                  transition: 'all 120ms',
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.color = 'var(--danger)';
                  e.currentTarget.style.borderColor = 'var(--danger-border)';
                  e.currentTarget.style.background = 'var(--danger-bg)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.color = 'var(--text-tertiary)';
                  e.currentTarget.style.borderColor = 'transparent';
                  e.currentTarget.style.background = 'transparent';
                }}
              >
                ×
              </button>
            </div>
          ))}
        </div>
      )}
      <div
        onDragOver={(e) => { e.preventDefault(); if (!disabled) setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragOver(false);
          if (!disabled && e.dataTransfer.files.length > 0) {
            addFiles(Array.from(e.dataTransfer.files));
          }
        }}
        onClick={() => { if (!disabled) inputRef.current?.click(); }}
        style={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 3,
          padding: files.length > 0 ? '8px 12px' : '14px 12px',
          border: `1.5px dashed ${dragOver ? 'var(--accent)' : 'var(--border-strong)'}`,
          borderRadius: 'var(--radius-md)',
          background: dragOver ? 'var(--accent-bg)' : 'transparent',
          cursor: disabled ? 'default' : 'pointer',
          transition: 'all 150ms ease',
        }}
      >
        <input
          ref={inputRef}
          type="file"
          multiple
          accept={ACCEPT}
          onChange={(e) => {
            if (e.target.files) addFiles(Array.from(e.target.files));
            e.target.value = '';
          }}
          disabled={disabled}
          style={{ display: 'none' }}
        />
        <span style={{
          fontSize: 11,
          color: dragOver ? 'var(--accent)' : 'var(--text-secondary)',
          fontWeight: 500,
          transition: 'color 150ms',
        }}>
          {dragOver ? 'Drop files here' : 'Drop files or click to browse'}
        </span>
        <span style={{
          fontSize: 9,
          fontFamily: 'var(--font-mono)',
          color: 'var(--text-tertiary)',
          letterSpacing: 0.3,
        }}>
          .md · .txt · .json · .csv · .yaml · .xml
        </span>
      </div>
    </div>
  );
}
