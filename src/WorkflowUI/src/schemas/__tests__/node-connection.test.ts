import { describe, it, expect } from 'vitest';
import { nodeConnectionSchema } from '../node-connection.schema';

describe('nodeConnectionSchema', () => {
  it('parses minimal connection', () => {
    const result = nodeConnectionSchema.parse({
      sourceNodeId: 'a',
      targetNodeId: 'b',
    });
    expect(result.triggerMessageType).toBe('Complete');
    expect(result.isEnabled).toBe(true);
    expect(result.priority).toBe(0);
  });

  it('rejects empty sourceNodeId', () => {
    const result = nodeConnectionSchema.safeParse({
      sourceNodeId: '',
      targetNodeId: 'b',
    });
    expect(result.success).toBe(false);
  });

  it('accepts all 9 properties', () => {
    const result = nodeConnectionSchema.parse({
      sourceNodeId: 'a',
      targetNodeId: 'b',
      triggerMessageType: 'Next',
      condition: 'true',
      isEnabled: false,
      metadata: { key: 'value' },
      priority: 1,
      sourcePort: 'out',
      targetPort: 'in',
    });
    expect(result.condition).toBe('true');
    expect(result.sourcePort).toBe('out');
  });

  it('rejects extra keys (strict)', () => {
    const result = nodeConnectionSchema.safeParse({
      sourceNodeId: 'a',
      targetNodeId: 'b',
      extraKey: true,
    });
    expect(result.success).toBe(false);
  });

  it('has 9 keys in shape', () => {
    expect(Object.keys(nodeConnectionSchema.shape)).toHaveLength(9);
  });

  it('rejects invalid messageType', () => {
    const result = nodeConnectionSchema.safeParse({
      sourceNodeId: 'a',
      targetNodeId: 'b',
      triggerMessageType: 'Invalid',
    });
    expect(result.success).toBe(false);
  });
});
