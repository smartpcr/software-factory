import { useLogStore } from './logStore';
import { useGraphStore } from '../state/graphStore';
import './LogPanel.css';

export function LogPanel() {
  const entries = useLogStore((s) => s.entries);
  const clear = useLogStore((s) => s.clear);
  const nodes = useGraphStore((s) => s.nodes);

  const getNodeName = (nodeId: string | null): string => {
    if (!nodeId) return 'system';
    const node = nodes.find((n) => n.nodeId === nodeId);
    return node?.nodeName || nodeId;
  };

  return (
    <div className="log-panel" data-testid="log-panel" role="log" aria-label="Execution log">
      <div className="log-header">
        <span className="log-title">Execution Log</span>
        <span className="log-count mono" data-testid="log-count">{entries.length}</span>
        <div style={{ flex: 1 }} />
        <button className="log-clear" onClick={clear} data-testid="log-clear" aria-label="Clear execution log">
          Clear
        </button>
      </div>
      <div className="log-body" data-testid="log-body">
        {entries.length === 0 ? (
          <div className="log-empty">No execution logs yet. Hit Run to start.</div>
        ) : (
          [...entries].reverse().map((entry, i) => (
            <div key={entries.length - i} className="log-line">
              <span className="log-time">{entry.timestamp}</span>
              <span className={`log-level ${entry.level}`}>{entry.level}</span>
              <span className="log-node">[{getNodeName(entry.nodeId)}]</span>
              <span className="log-msg">{entry.msg}</span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
