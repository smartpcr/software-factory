import { useState } from 'react';
import type { NodeConnectionDto, MessageType } from '../../engine-contract';
import { useGraphStore } from '../state/graphStore';

export interface EdgeInspectorProps {
  connection: NodeConnectionDto;
  onClose: () => void;
}

export function EdgeInspector({ connection, onClose }: EdgeInspectorProps) {
  const nodes = useGraphStore((s) => s.nodes);
  const updateConnection = useGraphStore((s) => s.updateConnection);
  const removeConnection = useGraphStore((s) => s.removeConnection);

  const sourceNode = nodes.find((n) => n.nodeId === connection.sourceNodeId);
  const targetNode = nodes.find((n) => n.nodeId === connection.targetNodeId);

  const [messageType, setMessageType] = useState<MessageType>(connection.triggerMessageType);
  const [condition, setCondition] = useState(connection.condition ?? '');
  const [isEnabled, setIsEnabled] = useState(connection.isEnabled);
  const [priority, setPriority] = useState(connection.priority);

  const handleSave = () => {
    updateConnection(connection.sourceNodeId, connection.targetNodeId, {
      triggerMessageType: messageType,
      condition: condition || undefined,
      isEnabled,
      priority,
    });
    onClose();
  };

  const handleDelete = () => {
    removeConnection(connection.sourceNodeId, connection.targetNodeId);
    onClose();
  };

  return (
    <div data-testid="edge-inspector">
      <div className="ins-header">
        <div className="ins-title">
          <div className="ins-title-text">Edge</div>
        </div>
        <div className="ins-subtitle mono">
          {sourceNode?.nodeName || connection.sourceNodeId} → {targetNode?.nodeName || connection.targetNodeId}
        </div>
      </div>

      <div className="ins-section">
        <div className="ins-section-title">Connection</div>
        <div className="ins-field">
          <label className="ins-field-label">Message Type</label>
          <select
            value={messageType}
            onChange={(e) => setMessageType(e.target.value as MessageType)}
            data-testid="edge-message-type"
          >
            <option value="Complete">Complete</option>
            <option value="Fail">Fail</option>
            <option value="Progress">Progress</option>
            <option value="Next">Next</option>
            <option value="Cancel">Cancel</option>
          </select>
        </div>
        <div className="ins-field">
          <label className="ins-field-label">Condition</label>
          <input
            type="text"
            value={condition}
            onChange={(e) => setCondition(e.target.value)}
            data-testid="edge-condition"
            placeholder="Optional expression"
          />
        </div>
        <div className="ins-field">
          <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12 }}>
            <input
              type="checkbox"
              checked={isEnabled}
              onChange={(e) => setIsEnabled(e.target.checked)}
              data-testid="edge-enabled"
              style={{ width: 'auto' }}
            />
            Enabled
          </label>
        </div>
        <div className="ins-field">
          <label className="ins-field-label">Priority</label>
          <input
            type="number"
            value={priority}
            onChange={(e) => setPriority(Number(e.target.value))}
            data-testid="edge-priority"
          />
        </div>
      </div>

      <div className="ins-section" style={{ display: 'flex', gap: 8 }}>
        <button type="button" className="tb-btn" onClick={handleSave} data-testid="edge-save" style={{ flex: 1 }}>
          Apply
        </button>
        <button type="button" className="ins-delete-btn" onClick={handleDelete} data-testid="edge-delete" style={{ flex: 1 }}>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
          </svg>
          Delete
        </button>
      </div>
    </div>
  );
}
