import { describe, it, expect } from 'vitest';
import { ifElseNodeSchema } from '../node-ifelse.schema';
import { switchNodeSchema } from '../node-switch.schema';
import { forEachNodeSchema } from '../node-foreach.schema';
import { whileNodeSchema } from '../node-while.schema';

describe('Control-flow kind schemas', () => {
  describe('ifElseNodeSchema', () => {
    it('parses valid condition', () => {
      const result = ifElseNodeSchema.parse({ runtimeType: 'IfElse', nodeId: 'n1', condition: 'x > 0' });
      expect(result.condition).toBe('x > 0');
    });

    it('rejects empty condition', () => {
      const result = ifElseNodeSchema.safeParse({ runtimeType: 'IfElse', nodeId: 'n1', condition: '' });
      expect(result.success).toBe(false);
    });

    it('rejects missing condition', () => {
      const result = ifElseNodeSchema.safeParse({ runtimeType: 'IfElse', nodeId: 'n1' });
      expect(result.success).toBe(false);
    });

    it('rejects trueBranch key via strict', () => {
      const result = ifElseNodeSchema.safeParse({ runtimeType: 'IfElse', nodeId: 'n1', condition: 'x > 0', trueBranch: 'node-a' });
      expect(result.success).toBe(false);
    });

    it('accepts condition with quotes', () => {
      const result = ifElseNodeSchema.parse({ runtimeType: 'IfElse', nodeId: 'n1', condition: "name == 'hello' && count > 0" });
      expect(result.condition).toBe("name == 'hello' && count > 0");
    });

    it('has exactly 1 extra property beyond base', () => {
      const baseKeys = 12;
      expect(Object.keys(ifElseNodeSchema.shape).length).toBe(baseKeys + 1);
    });
  });

  describe('switchNodeSchema', () => {
    it('parses with expression and cases', () => {
      const result = switchNodeSchema.parse({ runtimeType: 'Switch', nodeId: 'n1', expression: 'status', cases: { ready: 'Continue', error: 'Fail' } });
      expect(result.expression).toBe('status');
      expect(result.cases['ready']).toBe('Continue');
    });

    it('rejects empty expression', () => {
      const result = switchNodeSchema.safeParse({ runtimeType: 'Switch', nodeId: 'n1', expression: '' });
      expect(result.success).toBe(false);
    });

    it('accepts empty cases (draft)', () => {
      const result = switchNodeSchema.parse({ runtimeType: 'Switch', nodeId: 'n1', expression: 'x' });
      expect(result.cases).toEqual({});
    });

    it('rejects non-string case value', () => {
      const result = switchNodeSchema.safeParse({ runtimeType: 'Switch', nodeId: 'n1', expression: 'x', cases: { ready: 123 } });
      expect(result.success).toBe(false);
    });

    it('rejects defaultCase via strict', () => {
      const result = switchNodeSchema.safeParse({ runtimeType: 'Switch', nodeId: 'n1', expression: 'x', defaultCase: 'fallback' });
      expect(result.success).toBe(false);
    });

    it('has exactly 2 extras beyond base', () => {
      const baseKeys = 12;
      expect(Object.keys(switchNodeSchema.shape).length).toBe(baseKeys + 2);
    });
  });

  describe('forEachNodeSchema', () => {
    it('defaults itemVariableName to "item"', () => {
      const result = forEachNodeSchema.parse({ runtimeType: 'ForEach', nodeId: 'n1', collectionExpression: 'data.items' });
      expect(result.itemVariableName).toBe('item');
    });

    it('rejects empty collectionExpression', () => {
      const result = forEachNodeSchema.safeParse({ runtimeType: 'ForEach', nodeId: 'n1', collectionExpression: '' });
      expect(result.success).toBe(false);
    });

    it('rejects empty itemVariableName', () => {
      const result = forEachNodeSchema.safeParse({ runtimeType: 'ForEach', nodeId: 'n1', collectionExpression: 'data', itemVariableName: '' });
      expect(result.success).toBe(false);
    });

    it('accepts custom itemVariableName', () => {
      const result = forEachNodeSchema.parse({ runtimeType: 'ForEach', nodeId: 'n1', collectionExpression: 'records', itemVariableName: 'record' });
      expect(result.itemVariableName).toBe('record');
    });

    it('rejects extra key via strict', () => {
      const result = forEachNodeSchema.safeParse({ runtimeType: 'ForEach', nodeId: 'n1', collectionExpression: 'data', bodyNodeId: 'bad' });
      expect(result.success).toBe(false);
    });

    it('has exactly 2 extras beyond base', () => {
      const baseKeys = 12;
      expect(Object.keys(forEachNodeSchema.shape).length).toBe(baseKeys + 2);
    });
  });

  describe('whileNodeSchema', () => {
    it('defaults maxIterations to 1000', () => {
      const result = whileNodeSchema.parse({ runtimeType: 'While', nodeId: 'n1', conditionExpression: 'count < 10' });
      expect(result.maxIterations).toBe(1000);
    });

    it('rejects empty conditionExpression', () => {
      const result = whileNodeSchema.safeParse({ runtimeType: 'While', nodeId: 'n1', conditionExpression: '' });
      expect(result.success).toBe(false);
    });

    it('rejects maxIterations: 0', () => {
      const result = whileNodeSchema.safeParse({ runtimeType: 'While', nodeId: 'n1', conditionExpression: 'x', maxIterations: 0 });
      expect(result.success).toBe(false);
    });

    it('rejects maxIterations > 1_000_000', () => {
      const result = whileNodeSchema.safeParse({ runtimeType: 'While', nodeId: 'n1', conditionExpression: 'x', maxIterations: 1_000_001 });
      expect(result.success).toBe(false);
    });

    it('accepts maxIterations: 1', () => {
      const result = whileNodeSchema.parse({ runtimeType: 'While', nodeId: 'n1', conditionExpression: 'x', maxIterations: 1 });
      expect(result.maxIterations).toBe(1);
    });

    it('has exactly 2 extras beyond base', () => {
      const baseKeys = 12;
      expect(Object.keys(whileNodeSchema.shape).length).toBe(baseKeys + 2);
    });
  });
});
