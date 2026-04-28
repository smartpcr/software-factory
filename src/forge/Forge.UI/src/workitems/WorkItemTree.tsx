import { useMemo, useState } from 'react';
import type { WorkItem } from './workItemTypes';
import { typeRank } from './workItemTypes';
import { useWitStore } from './useWitStore';
import { TypeMark } from './TypeMark';
import { StatePill } from './StatePill';
import { ExecBadge } from './ExecBadge';
import { RepoIcon } from '@/products/RepoIcon';
import { SAMPLE_REPOS } from './sampleWorkItems';

interface Props {
  items: WorkItem[];
}

interface Node {
  item: WorkItem;
  children: Node[];
}

// Phase 12 — Tree mode. Indented hierarchy with a left guide rule and
// dash-leading markers (screenplay/manuscript feel) instead of generic
// chevrons. Items without a parent become roots; cycle-safe by construction
// because we walk the parent chain through a guard map.
export function WorkItemTree({ items }: Props) {
  const roots = useMemo(() => buildForest(items), [items]);
  return (
    <div className="wit-tree">
      <div className="wit-tree-rule" aria-hidden />
      {roots.length === 0 && <EmptyTree />}
      {roots.map((n) => <TreeNode key={n.item.id} node={n} depth={0} />)}
    </div>
  );
}

function EmptyTree() {
  return (
    <div className="wit-tree-empty">
      <div className="rule" />
      <p>No work items match these filters.</p>
    </div>
  );
}

function TreeNode({ node, depth }: { node: Node; depth: number }) {
  const [open, setOpen] = useState(depth < 2);
  const focus = useWitStore((s) => s.focus);
  const focusedId = useWitStore((s) => s.focusedId);
  const repo = SAMPLE_REPOS.find((r) => r.id === node.item.repoId);
  const focused = focusedId === node.item.id;

  return (
    <div className="wit-tree-node" style={{ '--depth': depth } as React.CSSProperties}>
      <div className={`wit-tree-row${focused ? ' focused' : ''}`}>
        <button
          type="button"
          className="wit-tree-toggle"
          onClick={() => setOpen((v) => !v)}
          aria-label={open ? 'Collapse' : 'Expand'}
          disabled={node.children.length === 0}
        >
          {node.children.length > 0 ? (open ? '▾' : '▸') : '·'}
        </button>
        <span className="wit-tree-dash" aria-hidden>
          {depth === 0 ? '§' : '—'}
        </span>
        <TypeMark type={node.item.type} size={18} />
        <button
          type="button"
          className="wit-tree-id"
          onClick={() => focus(node.item.id)}
          title="Open detail panel"
        >
          {node.item.id}
        </button>
        <span className="wit-tree-title" onClick={() => focus(node.item.id)}>
          {node.item.title}
        </span>
        <span className="wit-tree-fill" />
        <StatePill state={node.item.state} size="sm" />
        <ExecBadge status={node.item.executionStatus} trialCount={node.item.trialCount} size="sm" />
        {node.item.points > 0 && (
          <span className="wit-tree-pts">{node.item.points}<span>pt</span></span>
        )}
        {node.item.assignedTo && (
          <span className="wit-tree-avatar" title={node.item.assignedTo.displayName}>
            {node.item.assignedTo.initials}
          </span>
        )}
        {repo && <RepoIcon kind={repo.kind} size={12} title={repo.displayName} />}
      </div>
      {open && node.children.length > 0 && (
        <div className="wit-tree-children">
          {node.children.map((c) => <TreeNode key={c.item.id} node={c} depth={depth + 1} />)}
        </div>
      )}
    </div>
  );
}

function buildForest(items: WorkItem[]): Node[] {
  // Build a map; every item becomes a node. Children attach to their parent
  // when present. Items whose parentId points to something not in the visible
  // slice surface as roots (so filtered-out parents don't orphan whole trees).
  const idSet = new Set(items.map((i) => i.id));
  const byId = new Map<string, Node>();
  for (const it of items) byId.set(it.id, { item: it, children: [] });
  const roots: Node[] = [];
  for (const it of items) {
    const node = byId.get(it.id)!;
    if (it.parentId && idSet.has(it.parentId)) {
      byId.get(it.parentId)!.children.push(node);
    } else {
      roots.push(node);
    }
  }
  // Stable order: type rank then title.
  const sortNode = (n: Node) => {
    n.children.sort((a, b) => {
      const r = typeRank(a.item.type) - typeRank(b.item.type);
      return r !== 0 ? r : a.item.title.localeCompare(b.item.title);
    });
    n.children.forEach(sortNode);
  };
  roots.sort((a, b) => {
    const r = typeRank(a.item.type) - typeRank(b.item.type);
    return r !== 0 ? r : a.item.title.localeCompare(b.item.title);
  });
  roots.forEach(sortNode);
  return roots;
}
