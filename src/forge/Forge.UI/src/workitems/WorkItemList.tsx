import { useMemo, useState } from 'react';
import type { WorkItem } from './workItemTypes';
import { typeLabel } from './workItemTypes';
import { useWitStore, PRIORITY_WEIGHT } from './useWitStore';
import { TypeMark } from './TypeMark';
import { StatePill } from './StatePill';
import { ExecBadge } from './ExecBadge';
import { RepoIcon } from '@/products/RepoIcon';
import { SAMPLE_ITERATIONS } from './sampleWorkItems';
import { useRepoLookup } from './useRepoLookup';

interface Props {
  items: WorkItem[];
}

type SortKey = 'id' | 'type' | 'title' | 'state' | 'iteration' | 'assignee' | 'points' | 'priority';

// Phase 12 — List mode. Dense table with monospace numerics, sort by column,
// shift-click range select. Borrow the eyebrow-numbered column heads so this
// stays in the editorial register instead of feeling like a generic data
// grid.
export function WorkItemList({ items }: Props) {
  const [sortKey, setSortKey] = useState<SortKey>('id');
  const [sortDir, setSortDir] = useState<1 | -1>(1);
  const [lastClicked, setLastClicked] = useState<string | null>(null);
  const focus = useWitStore((s) => s.focus);
  const focusedId = useWitStore((s) => s.focusedId);
  const selection = useWitStore((s) => s.selection);
  const toggleSelect = useWitStore((s) => s.toggleSelect);
  const selectMany = useWitStore((s) => s.selectMany);
  const repoOf = useRepoLookup();

  const sorted = useMemo(() => {
    const copy = [...items];
    copy.sort((a, b) => {
      const av = sortValue(a, sortKey);
      const bv = sortValue(b, sortKey);
      if (av === bv) return a.id.localeCompare(b.id);
      return av > bv ? sortDir : -sortDir;
    });
    return copy;
  }, [items, sortKey, sortDir]);

  function onSort(k: SortKey) {
    if (sortKey === k) setSortDir(sortDir === 1 ? -1 : 1);
    else { setSortKey(k); setSortDir(1); }
  }

  function onRowClick(item: WorkItem, e: React.MouseEvent) {
    if (e.shiftKey && lastClicked) {
      const ai = sorted.findIndex((i) => i.id === lastClicked);
      const bi = sorted.findIndex((i) => i.id === item.id);
      if (ai >= 0 && bi >= 0) {
        const [from, to] = ai < bi ? [ai, bi] : [bi, ai];
        selectMany(sorted.slice(from, to + 1).map((i) => i.id));
        return;
      }
    }
    if (e.metaKey || e.ctrlKey) {
      toggleSelect(item.id, true);
      setLastClicked(item.id);
      return;
    }
    focus(item.id);
    setLastClicked(item.id);
  }

  return (
    <div className="wit-list-wrap">
      <table className="wit-list">
        <thead>
          <tr>
            <th className="wit-list-num">№</th>
            <SortHead k="id"        label="ID"        active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="type"      label="Type"      active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="title"     label="Title"     active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="state"     label="State"     active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="iteration" label="Iteration" active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="assignee"  label="Assignee"  active={sortKey} dir={sortDir} onClick={onSort} />
            <SortHead k="points"    label="Pts"       active={sortKey} dir={sortDir} onClick={onSort} numeric />
            <SortHead k="priority"  label="Pri"       active={sortKey} dir={sortDir} onClick={onSort} />
            <th>Repo</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((item, idx) => {
            const repo = repoOf(item.repoId);
            const it = SAMPLE_ITERATIONS.find((s) => s.ref === item.iterationRef);
            const selected = selection.has(item.id);
            const focused = focusedId === item.id;
            return (
              <tr
                key={item.id}
                className={`${selected ? 'selected' : ''} ${focused ? 'focused' : ''}`}
                onClick={(e) => onRowClick(item, e)}
                tabIndex={0}
                onKeyDown={(e) => { if (e.key === 'Enter') focus(item.id); }}
              >
                <td className="wit-list-num">{String(idx + 1).padStart(3, '0')}</td>
                <td className="wit-list-id">{item.id}</td>
                <td>
                  <span className="wit-list-type">
                    <TypeMark type={item.type} size={16} />
                    <span>{typeLabel(item.type)}</span>
                  </span>
                </td>
                <td className="wit-list-title">
                  {item.blocked && <span className="wit-list-blocked-mark" title="Blocked">!</span>}
                  {item.title}
                </td>
                <td><StatePill state={item.state} size="sm" />{' '}<ExecBadge status={item.executionStatus} trialCount={item.trialCount} size="sm" /></td>
                <td className="wit-list-iter">{it?.label ?? '—'}</td>
                <td className="wit-list-assignee">
                  {item.assignedTo ? (
                    <>
                      <span className="wit-list-avatar">{item.assignedTo.initials}</span>
                      <span>{item.assignedTo.displayName}</span>
                    </>
                  ) : <span className="muted">— unassigned</span>}
                </td>
                <td className="wit-list-num">{item.points || '—'}</td>
                <td><span className={`wit-list-pri pri-${item.priority}`}>{item.priority}</span></td>
                <td className="wit-list-repo">
                  {repo ? (
                    <span className="wit-list-repo-cell" title={`${repo.displayName} (${repo.kind === 'ado' ? 'Azure DevOps' : 'GitHub'})`}>
                      <RepoIcon kind={repo.kind} size={12} />
                      <span>{repo.displayName}</span>
                    </span>
                  ) : item.repoId ? (
                    <span className="wit-list-repo-cell muted" title={item.repoId}>{item.repoId}</span>
                  ) : (
                    <span className="muted">—</span>
                  )}
                </td>
              </tr>
            );
          })}
          {sorted.length === 0 && (
            <tr>
              <td colSpan={10} className="wit-list-empty">No work items match these filters.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

interface SortHeadProps {
  k: SortKey;
  label: string;
  active: SortKey;
  dir: 1 | -1;
  onClick: (k: SortKey) => void;
  numeric?: boolean;
}

function SortHead({ k, label, active, dir, onClick, numeric }: SortHeadProps) {
  const isActive = active === k;
  return (
    <th className={`wit-list-sort${numeric ? ' wit-list-num' : ''}${isActive ? ' active' : ''}`}>
      <button type="button" onClick={() => onClick(k)}>
        <span>{label}</span>
        <span className="arrow" aria-hidden>{isActive ? (dir === 1 ? '▲' : '▼') : '·'}</span>
      </button>
    </th>
  );
}

function sortValue(i: WorkItem, k: SortKey): string | number {
  switch (k) {
    case 'id': return i.id;
    case 'type': return i.type;
    case 'title': return i.title.toLowerCase();
    case 'state': return ['proposed', 'active', 'in-review', 'resolved', 'closed'].indexOf(i.state);
    case 'iteration': return i.iterationRef;
    case 'assignee': return i.assignedTo?.displayName ?? '~~unassigned';
    case 'points': return i.points;
    case 'priority': return PRIORITY_WEIGHT[i.priority];
  }
}
