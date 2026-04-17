import { describe, it, expect } from 'vitest';
import { nodeDtoSchema } from '../node-union';
import { timerNodeSchema } from '../node-timer.schema';

describe('Composition kind schemas', () => {
  describe('subflowNodeSchema (via union)', () => {
    it('accepts external subflow', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
        workflowFilePath: 'child.yaml',
      });
      expect(result.success).toBe(true);
      if (result.success) {
        expect(result.data.workflowFilePath).toBe('child.yaml');
      }
    });

    it('accepts inline subflow', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
        workflowDefinition: {
          workflowId: 'inline',
          nodes: [{ runtimeType: 'Noop', nodeId: 'n1' }],
          connections: [],
        },
      });
      expect(result.success).toBe(true);
    });

    it('rejects both path and definition', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
        workflowFilePath: 'child.yaml',
        workflowDefinition: { workflowId: 'inline', nodes: [], connections: [] },
      });
      expect(result.success).toBe(false);
    });

    it('rejects neither path nor definition', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
      });
      expect(result.success).toBe(false);
    });

    it('defaults timeout to 00:05:00', () => {
      const result = nodeDtoSchema.parse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
        workflowFilePath: 'child.yaml',
      });
      expect(result.timeout).toBe('00:05:00');
    });

    it('defaults skipValidation to false', () => {
      const result = nodeDtoSchema.parse({
        runtimeType: 'Subflow',
        nodeId: 'sf1',
        workflowFilePath: 'child.yaml',
      });
      expect(result.skipValidation).toBe(false);
    });
  });

  describe('timerNodeSchema', () => {
    it('accepts CRON 5-field', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: '*/5 * * * *' });
      expect(result.success).toBe(true);
    });

    it('accepts CRON 6-field', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: '30 0 9 * * 1-5' });
      expect(result.success).toBe(true);
    });

    it('accepts ISO duration', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: 'PT5M' });
      expect(result.success).toBe(true);
    });

    it('accepts hh:mm:ss duration', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: '01:30:00' });
      expect(result.success).toBe(true);
    });

    it('rejects invalid schedule', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: 'not-a-schedule' });
      expect(result.success).toBe(false);
    });

    it('rejects empty schedule', () => {
      const result = timerNodeSchema.safeParse({ runtimeType: 'Timer', nodeId: 't1', schedule: '' });
      expect(result.success).toBe(false);
    });

    it('has exactly 1 extra beyond base', () => {
      const baseKeys = 12;
      expect(Object.keys(timerNodeSchema.shape).length).toBe(baseKeys + 1);
    });
  });

  describe('containerNodeSchema (via union)', () => {
    it('accepts sequential container', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Container',
        nodeId: 'c1',
        executionMode: 'Sequential',
      });
      expect(result.success).toBe(true);
    });

    it('accepts parallel container', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Container',
        nodeId: 'c1',
        executionMode: 'Parallel',
      });
      expect(result.success).toBe(true);
    });

    it('defaults executionMode to Sequential', () => {
      const result = nodeDtoSchema.parse({ runtimeType: 'Container', nodeId: 'c1' });
      expect(result.executionMode).toBe('Sequential');
    });

    it('accepts nested containers', () => {
      const result = nodeDtoSchema.safeParse({
        runtimeType: 'Container',
        nodeId: 'c1',
        childNodes: [{
          runtimeType: 'Container',
          nodeId: 'c2',
          childNodes: [{ runtimeType: 'Noop', nodeId: 'n1' }],
        }],
      });
      expect(result.success).toBe(true);
    });
  });
});
