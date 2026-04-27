import { useEffect, useMemo, useState } from 'react';
import type { WorkItem, WorkItemPriority, WorkItemState } from './workItemTypes';
import { typeLabel, WORK_ITEM_STATES } from './workItemTypes';
import { useWitStore } from './useWitStore';
import { useWorkItemsStore } from './useWorkItemsStore';
import { TypeMark } from './TypeMark';
import { StatePill } from './StatePill';
import { RepoIcon } from '@/products/RepoIcon';
import { SAMPLE_ITERATIONS, SAMPLE_REPOS } from './sampleWorkItems';

interface Props {
  item: WorkItem | null;
}

// Phase 12 — right rail. Inline-edit shape for the implementation step is
// scaffolded with read-only displays today (sample data, no API). The layout
// is intentionally publication-style: a manuscript page with type-set
// metadata and a markdown preview body.
export function WorkItemDetailPanel({ item }: Props) {
  const focus = useWitStore((s) => s.focus);
  const expanded = useWitStore((s) => s.panelExpanded);
  const togglePanelExpanded = useWitStore((s) => s.togglePanelExpanded);
  const allItems = useWorkItemsStore((s) => s.items);
  const patch = useWorkItemsStore((s) => s.patch);

  // Local-edit buffers so we can submit on blur/Enter without re-rendering on every keystroke.
  const [titleDraft, setTitleDraft] = useState(item?.title ?? '');
  const [editingTitle, setEditingTitle] = useState(false);
  useEffect(() => { setTitleDraft(item?.title ?? ''); setEditingTitle(false); }, [item?.id, item?.title]);

  if (!item) {
    return (
      <aside className="wit-detail wit-detail-empty">
        <div className="wit-detail-empty-mark">§</div>
        <div className="wit-detail-empty-title">Select an item</div>
        <div className="wit-detail-empty-sub">
          Click any work item to read, edit, or re-parent. Multi-select with shift-click on Board / List for bulk operations.
        </div>
      </aside>
    );
  }

  const repo = SAMPLE_REPOS.find((r) => r.id === item.repoId);
  const iteration = SAMPLE_ITERATIONS.find((s) => s.ref === item.iterationRef);
  const breadcrumb = useMemo(() => ancestry(item, allItems), [item, allItems]);
  const children = useMemo(() => allItems.filter((c) => c.parentId === item.id), [item, allItems]);

  function commitTitle() {
    setEditingTitle(false);
    if (titleDraft.trim() && titleDraft !== item!.title) {
      void patch(item!.id, { title: titleDraft.trim() });
    } else {
      setTitleDraft(item!.title);
    }
  }

  function setState(s: WorkItemState) {
    if (s !== item!.state) void patch(item!.id, { state: s });
  }
  function setPriority(p: WorkItemPriority) {
    if (p !== item!.priority) void patch(item!.id, { priority: p });
  }
  function setPoints(p: number) {
    if (p !== item!.points && p >= 0) void patch(item!.id, { points: p });
  }

  return (
    <aside className={`wit-detail${expanded ? ' wit-detail-expanded' : ''}`}>
      <header className="wit-detail-head">
        <div className="wit-detail-meta-line">
          <TypeMark type={item.type} size={20} />
          <span className="wit-detail-id">{item.id}</span>
          {item.providerRef !== null && (
            <span className="wit-detail-provider-ref">
              {item.provider === 'ado' ? `WI #${item.providerRef}` : `#${item.providerRef}`}
            </span>
          )}
          {repo && <RepoIcon kind={repo.kind} size={14} title={repo.displayName} />}
          <div className="wit-detail-head-actions">
            <button
              type="button"
              className="wit-detail-icon-btn"
              onClick={togglePanelExpanded}
              aria-label={expanded ? 'Collapse panel' : 'Expand panel'}
              title={expanded ? 'Collapse panel (Esc)' : 'Expand to full screen'}
            >
              {expanded ? (
                <svg width="13" height="13" viewBox="0 0 14 14" fill="none" aria-hidden>
                  <path d="M3 3 H 6 V 6 M 3 3 L 7 7 M 11 11 H 8 V 8 M 11 11 L 7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              ) : (
                <svg width="13" height="13" viewBox="0 0 14 14" fill="none" aria-hidden>
                  <path d="M3 3 H 6 M 3 3 V 6 M 3 3 L 7 7 M 11 11 H 8 M 11 11 V 8 M 11 11 L 7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              )}
            </button>
            <button
              type="button"
              className="wit-detail-icon-btn wit-detail-close"
              onClick={() => focus(null)}
              aria-label="Close panel"
              title="Close panel (Esc)"
            >
              ×
            </button>
          </div>
        </div>
        {editingTitle ? (
          <input
            type="text"
            className="wit-detail-title-input"
            value={titleDraft}
            autoFocus
            onChange={(e) => setTitleDraft(e.target.value)}
            onBlur={commitTitle}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitTitle();
              if (e.key === 'Escape') { setTitleDraft(item.title); setEditingTitle(false); }
            }}
          />
        ) : (
          <h2
            className="wit-detail-title wit-detail-title-editable"
            onClick={() => setEditingTitle(true)}
            title="Click to edit"
          >
            {item.title}
          </h2>
        )}
        <div className="wit-detail-eyebrow-row">
          <StateSelect value={item.state} onChange={setState} />
          <span className="wit-detail-eyebrow-meta">
            <span className="lbl">Iteration</span>
            <span className="val">{iteration?.label ?? '—'}</span>
          </span>
          <PointsStepper value={item.points} onChange={setPoints} />
          <PrioritySelect value={item.priority} onChange={setPriority} />
        </div>
      </header>

      {breadcrumb.length > 0 && (
        <nav className="wit-detail-breadcrumb" aria-label="Hierarchy">
          {breadcrumb.map((p, i) => (
            <span key={p.id} className="wit-detail-breadcrumb-seg">
              <button type="button" onClick={() => focus(p.id)}>
                <TypeMark type={p.type} size={14} muted />
                <span className="seg-id">{p.id}</span>
                <span className="seg-title">{p.title}</span>
              </button>
              {i < breadcrumb.length - 1 && <span className="wit-detail-breadcrumb-sep">▸</span>}
            </span>
          ))}
        </nav>
      )}

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Assignee</div>
        {item.assignedTo ? (
          <div className="wit-detail-assignee">
            <span className="wit-detail-avatar">{item.assignedTo.initials}</span>
            <div>
              <div className="wit-detail-assignee-name">{item.assignedTo.displayName}</div>
              <div className="wit-detail-assignee-kind">
                {item.assignedTo.kind} · {item.assignedTo.id}
              </div>
            </div>
          </div>
        ) : (
          <div className="muted">— unassigned</div>
        )}
      </section>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Description</div>
        <MarkdownPreview md={item.descriptionMd} />
      </section>

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">
          Tags
          <span className="wit-detail-section-count">{item.tags.length}</span>
        </div>
        <div className="wit-detail-tags">
          {item.tags.length === 0 && <span className="muted">— none</span>}
          {item.tags.map((t) => <span key={t} className="wit-tag">{t}</span>)}
        </div>
      </section>

      {children.length > 0 && (
        <section className="wit-detail-section">
          <div className="wit-detail-section-title">
            Children
            <span className="wit-detail-section-count">{children.length}</span>
          </div>
          <ul className="wit-detail-children">
            {children.map((c) => (
              <li key={c.id}>
                <button type="button" onClick={() => focus(c.id)}>
                  <TypeMark type={c.type} size={14} />
                  <span className="cid">{c.id}</span>
                  <span className="ctitle">{c.title}</span>
                  <StatePill state={c.state} size="sm" />
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="wit-detail-section">
        <div className="wit-detail-section-title">Provenance</div>
        <dl className="wit-detail-provenance">
          <dt>Area</dt><dd>{item.areaPath}</dd>
          <dt>Repo</dt>
          <dd>
            {repo ? (
              <span className="wit-detail-repo">
                <RepoIcon kind={repo.kind} size={12} />
                {repo.displayName} · {repo.kind === 'ado' ? 'Azure DevOps' : 'GitHub'}
              </span>
            ) : '—'}
          </dd>
          <dt>Created</dt><dd>{fmt(item.createdAt)}</dd>
          <dt>Updated</dt><dd>{fmt(item.updatedAt)}</dd>
          <dt>Type</dt><dd>{typeLabel(item.type)}</dd>
        </dl>
      </section>

      <footer className="wit-detail-foot">
        <span className="wit-detail-foot-hint">
          Inline edit on title / state / points / priority is live. Iteration combo + assignee picker + tag editor land with Phase 12.7 follow-up.
        </span>
      </footer>
    </aside>
  );
}

// ---- inline-edit sub-components ----------------------------------------

function StateSelect({ value, onChange }: { value: WorkItemState; onChange: (s: WorkItemState) => void }) {
  return (
    <label className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">State</span>
      <select
        className="wit-detail-state-select"
        value={value}
        onChange={(e) => onChange(e.target.value as WorkItemState)}
        aria-label="Work item state"
      >
        {WORK_ITEM_STATES.map((s) => (
          <option key={s} value={s}>{s}</option>
        ))}
      </select>
    </label>
  );
}

function PointsStepper({ value, onChange }: { value: number; onChange: (n: number) => void }) {
  return (
    <span className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">Points</span>
      <span className="wit-detail-points-stepper">
        <button
          type="button"
          aria-label="Decrement points"
          disabled={value <= 0}
          onClick={() => onChange(Math.max(0, value - 1))}
        >−</button>
        <span className="val">{value || '—'}</span>
        <button
          type="button"
          aria-label="Increment points"
          onClick={() => onChange(value + 1)}
        >+</button>
      </span>
    </span>
  );
}

function PrioritySelect({ value, onChange }: { value: WorkItemPriority; onChange: (p: WorkItemPriority) => void }) {
  const options: WorkItemPriority[] = ['low', 'med', 'high'];
  return (
    <span className="wit-detail-eyebrow-meta wit-detail-edit-shell">
      <span className="lbl">Priority</span>
      <span className="wit-detail-pri-chips" role="radiogroup" aria-label="Priority">
        {options.map((p) => (
          <button
            key={p}
            type="button"
            role="radio"
            aria-checked={value === p}
            className={`wit-detail-pri-chip pri-${p}${value === p ? ' active' : ''}`}
            onClick={() => onChange(p)}
          >
            {p}
          </button>
        ))}
      </span>
    </span>
  );
}

function ancestry(item: WorkItem, all: WorkItem[]): WorkItem[] {
  const out: WorkItem[] = [];
  const seen = new Set<string>();
  let cur: WorkItem | null = item.parentId
    ? all.find((w) => w.id === item.parentId) ?? null
    : null;
  while (cur && !seen.has(cur.id)) {
    seen.add(cur.id);
    out.unshift(cur);
    cur = cur.parentId ? all.find((w) => w.id === cur!.parentId) ?? null : null;
  }
  return out;
}

function fmt(iso: string): string {
  const d = new Date(iso);
  return `${d.toISOString().slice(0, 10)} ${d.toISOString().slice(11, 16)}`;
}

// Tiny inline markdown preview — handles headings, bold, italic, inline
// code, and paragraphs. Enough for the read-only mockup; the real editor
// arrives with Phase 12.7.
function MarkdownPreview({ md }: { md: string }) {
  const html = useMemo(() => renderMd(md), [md]);
  if (!md.trim()) return <div className="muted">— no description</div>;
  return <div className="wit-detail-md" dangerouslySetInnerHTML={{ __html: html }} />;
}

function renderMd(md: string): string {
  // Escape first.
  const esc = (s: string) => s
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
  const lines = esc(md).split('\n');
  const out: string[] = [];
  let para: string[] = [];
  const flush = () => { if (para.length) { out.push(`<p>${inline(para.join(' '))}</p>`); para = []; } };
  for (const line of lines) {
    const t = line.trim();
    if (!t) { flush(); continue; }
    const h = /^(#{1,3})\s+(.+)$/.exec(t);
    if (h) {
      flush();
      const lvl = h[1].length;
      out.push(`<h${lvl}>${inline(h[2])}</h${lvl}>`);
      continue;
    }
    if (t.startsWith('- ')) {
      flush();
      out.push(`<ul><li>${inline(t.slice(2))}</li></ul>`);
      continue;
    }
    para.push(t);
  }
  flush();
  // Coalesce adjacent <ul>s — naive but enough for the mockup.
  return out.join('\n').replace(/<\/ul>\n<ul>/g, '');
}

function inline(s: string): string {
  return s
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, '<em>$1</em>')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/~~([^~]+)~~/g, '<del>$1</del>');
}
