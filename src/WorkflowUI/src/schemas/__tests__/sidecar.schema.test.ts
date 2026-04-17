import { describe, it, expect } from 'vitest';
import { sidecarSchema } from '../sidecar.schema';
import basic from '../../__fixtures__/sidecar-basic.json';
import missingNode from '../../__fixtures__/sidecar-missing-node.json';
import withNotes from '../../__fixtures__/sidecar-with-notes.json';

describe('sidecarSchema', () => {
  it('parses basic fixture', () => {
    const result = sidecarSchema.parse(basic);
    expect(result.version).toBe('1.0');
    expect(result.workflowFile).toBe('test.yaml');
  });

  it('parses missing-node fixture', () => {
    const result = sidecarSchema.parse(missingNode);
    expect(result.nodePositions['ghost']).toEqual({ x: 50, y: 50 });
  });

  it('parses with-notes fixture', () => {
    const result = sidecarSchema.parse(withNotes);
    expect(result.notes).toHaveLength(1);
    expect(result.notes[0].text).toBe('Review this node');
  });

  it('rejects invalid color', () => {
    const result = sidecarSchema.safeParse({
      version: '1.0',
      workflowFile: 'test.yaml',
      nodeColors: { n1: 'red' },
    });
    expect(result.success).toBe(false);
  });

  it('rejects missing version', () => {
    const result = sidecarSchema.safeParse({
      workflowFile: 'test.yaml',
    });
    expect(result.success).toBe(false);
  });

  it('accepts empty positions', () => {
    const result = sidecarSchema.parse({
      version: '1.0',
      workflowFile: 'test.yaml',
    });
    expect(result.nodePositions).toEqual({});
  });

  it('accepts fractional coordinates', () => {
    const result = sidecarSchema.parse({
      version: '1.0',
      workflowFile: 'test.yaml',
      nodePositions: { n1: { x: 10.5, y: -20.3 } },
    });
    expect(result.nodePositions['n1'].x).toBe(10.5);
  });

  it('accepts negative coordinates', () => {
    const result = sidecarSchema.parse({
      version: '1.0',
      workflowFile: 'test.yaml',
      nodePositions: { n1: { x: -100, y: -200 } },
    });
    expect(result.nodePositions['n1'].x).toBe(-100);
  });

  it('rejects extra keys (strict)', () => {
    const result = sidecarSchema.safeParse({
      version: '1.0',
      workflowFile: 'test.yaml',
      extraKey: true,
    });
    expect(result.success).toBe(false);
  });

  it('rejects wrong version', () => {
    const result = sidecarSchema.safeParse({
      version: '2.0',
      workflowFile: 'test.yaml',
    });
    expect(result.success).toBe(false);
  });

  it('validates note color format', () => {
    const result = sidecarSchema.safeParse({
      version: '1.0',
      workflowFile: 'test.yaml',
      notes: [{ id: 'n1', x: 0, y: 0, text: 'hi', color: 'blue' }],
    });
    expect(result.success).toBe(false);
  });
});
