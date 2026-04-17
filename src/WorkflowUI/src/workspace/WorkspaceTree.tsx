import { useCallback, useEffect, useState } from 'react';
import type { TreeNode } from '../api/files';
import {
  getWorkspaceTree,
  createWorkflow,
  renameWorkflow,
  deleteWorkflow,
} from '../api/files';

interface WorkspaceTreeProps {
  onSelect?: (workflowId: string) => void;
}

function TreeNodeRow({
  node,
  depth,
  onSelect,
  onRename,
  onDelete,
}: {
  node: TreeNode;
  depth: number;
  onSelect?: (workflowId: string) => void;
  onRename: (oldId: string, newId: string) => void;
  onDelete: (id: string) => void;
}) {
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState(node.name);

  const handleClick = () => {
    if (!node.isFolder && node.workflowId && onSelect) {
      onSelect(node.workflowId);
    }
  };

  const handleRenameSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (renameValue && renameValue !== node.name && node.workflowId) {
      onRename(node.workflowId, renameValue);
    }
    setRenaming(false);
  };

  return (
    <>
      <li
        role="treeitem"
        aria-expanded={node.isFolder ? true : undefined}
        style={{ paddingLeft: `${depth * 16}px` }}
        onClick={handleClick}
        data-testid={`tree-node-${node.name}`}
      >
        <span>{node.isFolder ? '📁' : '📄'}</span>
        {renaming ? (
          <form onSubmit={handleRenameSubmit} style={{ display: 'inline' }}>
            <input
              aria-label="Rename"
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              autoFocus
              onBlur={() => setRenaming(false)}
            />
          </form>
        ) : (
          <span
            onDoubleClick={() => {
              if (!node.isFolder) setRenaming(true);
            }}
          >
            {node.name}
          </span>
        )}
        {!node.isFolder && node.workflowId && (
          <span className="tree-actions">
            <button
              aria-label={`Rename ${node.name}`}
              onClick={(e) => {
                e.stopPropagation();
                setRenaming(true);
              }}
            >
              ✏️
            </button>
            <button
              aria-label={`Delete ${node.name}`}
              onClick={(e) => {
                e.stopPropagation();
                if (node.workflowId) {
                  onDelete(node.workflowId);
                }
              }}
            >
              🗑️
            </button>
          </span>
        )}
      </li>
      {node.isFolder &&
        node.children.map((child) => (
          <TreeNodeRow
            key={child.workflowId ?? child.name}
            node={child}
            depth={depth + 1}
            onSelect={onSelect}
            onRename={onRename}
            onDelete={onDelete}
          />
        ))}
    </>
  );
}

export function WorkspaceTree({ onSelect }: WorkspaceTreeProps) {
  const [tree, setTree] = useState<TreeNode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [newName, setNewName] = useState('');

  const refresh = useCallback(async () => {
    try {
      const root = await getWorkspaceTree();
      setTree(root);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load tree');
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!newName.trim()) return;
    try {
      await createWorkflow(newName.trim());
      setNewName('');
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Create failed');
    }
  };

  const handleRename = async (oldId: string, newId: string) => {
    try {
      await renameWorkflow(oldId, newId);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Rename failed');
    }
  };

  const handleDelete = async (id: string) => {
    try {
      await deleteWorkflow(id);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed');
    }
  };

  return (
    <div className="workspace-tree" role="tree" aria-label="Workspace tree">
      <form onSubmit={handleCreate}>
        <input
          aria-label="New workflow name"
          placeholder="New workflow name"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
        />
        <button type="submit" aria-label="Create workflow">
          + New
        </button>
      </form>
      {error && (
        <div role="alert" className="tree-error">
          {error}
        </div>
      )}
      <ul role="group">
        {tree?.children.map((child) => (
          <TreeNodeRow
            key={child.workflowId ?? child.name}
            node={child}
            depth={0}
            onSelect={onSelect}
            onRename={handleRename}
            onDelete={handleDelete}
          />
        ))}
      </ul>
    </div>
  );
}
