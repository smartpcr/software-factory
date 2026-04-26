import { useEffect, useState } from 'react';
import type { Card, ColumnId, Priority, CardType } from '@/types';
import { useStateStore } from '@/state/useStateStore';
import { useBoardStore } from '@/state/useBoardStore';
import { sampleState } from '@/sampleData';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

interface CardModalProps {
  open: boolean;
  onClose: () => void;
  /** Pass a card to edit it; omit for the "new card" flow. */
  initial?: Card | null;
}

const TYPES: CardType[] = ['story', 'task', 'bug'];
const PRIORITIES: Priority[] = ['low', 'med', 'high'];

// Phase-1 modal for create / edit. Visual style reuses the existing
// modal CSS (.modal, .modal-actions, .btn-*) — the prototype only defines
// one bespoke modal shape, so this is structural only.
export function CardModal({ open, onClose, initial }: CardModalProps) {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const upsert = useBoardStore((s) => s.upsertFromRealtime);

  const [id, setId] = useState('');
  const [epic, setEpic] = useState(epics[0]?.id ?? '');
  const [col, setCol] = useState<ColumnId>('backlog');
  const [type, setType] = useState<CardType>('story');
  const [title, setTitle] = useState('');
  const [pts, setPts] = useState(3);
  const [priority, setPriority] = useState<Priority>('med');
  const [tags, setTags] = useState('');
  const [postTo, setPostTo] = useState<'' | 'ado' | 'github'>('');
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    setId(initial?.id ?? '');
    setEpic(initial?.epic ?? epics[0]?.id ?? '');
    setCol(initial?.col ?? 'backlog');
    setType(initial?.type ?? 'story');
    setTitle(initial?.title ?? '');
    setPts(initial?.pts ?? 3);
    setPriority(initial?.priority ?? 'med');
    setTags((initial?.tags ?? []).join(', '));
    setPostTo('');
    setDescription('');
  }, [open, initial, epics]);

  async function submit() {
    setBusy(true);
    try {
      const tagArr = tags.split(',').map((t) => t.trim()).filter(Boolean);
      if (initial) {
        const updated = await forgeApi.patchCard(initial.id, { title, pts, priority, type, tags: tagArr });
        upsert(updated);
        useToastStore.getState().show(`${initial.id} updated`, 'success');
      } else {
        const newCard: Card = {
          id: id || `#${Math.floor(Math.random() * 9000 + 1000)}`,
          epic, col, type, title, tags: tagArr,
          pts, priority, agent: null, cost: 0,
        };
        const provider = postTo === '' ? undefined : postTo;
        const created = await forgeApi.createCard(newCard, provider, description || undefined);
        upsert(created);
        const where = provider ? ` on ${provider}` : '';
        useToastStore.getState().show(`${created.id} created${where}`, 'success');
      }
      onClose();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Save failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className={`modal-backdrop${open ? ' show' : ''}`} onClick={onClose} />
      <div className={`modal${open ? ' show' : ''}`} role="dialog" aria-modal="true">
        <h3 className="modal-title">{initial ? `Edit ${initial.id}` : 'New card'}</h3>
        <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
          {!initial && (
            <Field label="ID (optional)">
              <input value={id} onChange={(e) => setId(e.target.value)} placeholder="auto" />
            </Field>
          )}
          <Field label="Title">
            <input value={title} onChange={(e) => setTitle(e.target.value)} autoFocus />
          </Field>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <Field label="Epic">
              <select value={epic} onChange={(e) => setEpic(e.target.value)}>
                {epics.map((ep) => <option key={ep.id} value={ep.id}>{ep.id}</option>)}
              </select>
            </Field>
            <Field label="Column">
              <select value={col} onChange={(e) => setCol(e.target.value as ColumnId)}>
                {(['backlog','design','build','review','done'] as ColumnId[]).map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </Field>
            <Field label="Type">
              <select value={type} onChange={(e) => setType(e.target.value as CardType)}>
                {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
              </select>
            </Field>
            <Field label="Priority">
              <select value={priority} onChange={(e) => setPriority(e.target.value as Priority)}>
                {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </Field>
            <Field label="Points">
              <input type="number" min={0} max={21} value={pts} onChange={(e) => setPts(Number(e.target.value) || 0)} />
            </Field>
            <Field label="Tags (comma)">
              <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="auth, infra" />
            </Field>
          </div>
          {!initial && (
            <>
              <Field label="Post to provider (Phase 6.1)">
                <select value={postTo} onChange={(e) => setPostTo(e.target.value as '' | 'ado' | 'github')}>
                  <option value="">Local only</option>
                  <option value="ado">Azure DevOps</option>
                  <option value="github">GitHub</option>
                </select>
              </Field>
              {postTo !== '' && (
                <Field label="Description (markdown)">
                  <textarea
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    rows={4}
                    placeholder="Optional body for the new work item / issue."
                    style={{ resize: 'vertical', width: '100%' }}
                  />
                </Field>
              )}
            </>
          )}
        </div>
        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn btn-primary" onClick={submit} disabled={busy || !title}>
            {busy ? 'Saving…' : initial ? 'Save' : 'Create'}
          </button>
        </div>
      </div>
    </>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4, fontSize: 11, color: 'var(--text-secondary)' }}>
      <span>{label}</span>
      <span style={{
        display: 'block', padding: '6px 10px', borderRadius: 'var(--radius-sm)',
        border: '1px solid var(--border-strong)', background: 'var(--bg-surface)',
      }}>
        {children}
      </span>
    </label>
  );
}
