import { useState } from 'react';
import type { NodeConnectionDto, MessageType } from '../../engine-contract';

export interface EdgePopoverProps {
  connection: NodeConnectionDto;
  onSave: (updated: NodeConnectionDto) => void;
  onClose: () => void;
}

export function EdgePopover({ connection, onSave, onClose }: EdgePopoverProps) {
  const [condition, setCondition] = useState(connection.condition ?? '');
  const [messageType, setMessageType] = useState<MessageType>(connection.triggerMessageType);
  const [isEnabled, setIsEnabled] = useState(connection.isEnabled);
  const [priority, setPriority] = useState(connection.priority);

  const handleSave = () => {
    onSave({
      ...connection,
      condition: condition || undefined,
      triggerMessageType: messageType,
      isEnabled,
      priority,
    });
    onClose();
  };

  return (
    <div
      data-testid="edge-popover"
      role="dialog"
      aria-label="Edit connection"
      style={{
        background: '#fff',
        border: '1px solid #d1d5db',
        borderRadius: 8,
        padding: 12,
        minWidth: 240,
        boxShadow: '0 2px 4px rgba(0,0,0,0.1)',
      }}
    >
      <div style={{ marginBottom: 8 }}>
        <label style={{ fontSize: 12, fontWeight: 600 }}>
          Message Type
          <select
            value={messageType}
            onChange={(e) => setMessageType(e.target.value as MessageType)}
            data-testid="edge-message-type"
            style={{ display: 'block', width: '100%', marginTop: 2 }}
          >
            <option value="Complete">Complete</option>
            <option value="Fail">Fail</option>
            <option value="Progress">Progress</option>
            <option value="Next">Next</option>
            <option value="Cancel">Cancel</option>
          </select>
        </label>
      </div>
      <div style={{ marginBottom: 8 }}>
        <label style={{ fontSize: 12, fontWeight: 600 }}>
          Condition
          <input
            type="text"
            value={condition}
            onChange={(e) => setCondition(e.target.value)}
            data-testid="edge-condition"
            style={{ display: 'block', width: '100%', marginTop: 2, padding: '2px 4px' }}
          />
        </label>
      </div>
      <div style={{ marginBottom: 8 }}>
        <label style={{ fontSize: 12 }}>
          <input
            type="checkbox"
            checked={isEnabled}
            onChange={(e) => setIsEnabled(e.target.checked)}
            data-testid="edge-enabled"
          />
          {' '}Enabled
        </label>
      </div>
      <div style={{ marginBottom: 8 }}>
        <label style={{ fontSize: 12, fontWeight: 600 }}>
          Priority
          <input
            type="number"
            value={priority}
            onChange={(e) => setPriority(Number(e.target.value))}
            data-testid="edge-priority"
            style={{ display: 'block', width: '100%', marginTop: 2, padding: '2px 4px' }}
          />
        </label>
      </div>
      <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" onClick={onClose} data-testid="edge-cancel">Cancel</button>
        <button type="button" onClick={handleSave} data-testid="edge-save">Save</button>
      </div>
    </div>
  );
}