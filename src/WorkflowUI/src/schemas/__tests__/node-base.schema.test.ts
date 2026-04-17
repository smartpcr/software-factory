import { describe, it, expect } from 'vitest';
import { nodeBaseSchema } from '../node-base.schema';
import validFixture from '../../__fixtures__/node-base.valid.json';
import retryFixture from '../../__fixtures__/node-base.with-retry.json';
import cbFixture from '../../__fixtures__/node-base.with-circuit-breaker.json';
import nullPoliciesFixture from '../../__fixtures__/node-base.null-policies.json';

describe('nodeBaseSchema', () => {
  it('parses minimal fixture', () => {
    const result = nodeBaseSchema.parse(validFixture);
    expect(result.nodeId).toBe('n1');
    expect(result.runtimeType).toBe('Noop');
  });

  it('parses retry fixture', () => {
    const result = nodeBaseSchema.parse(retryFixture);
    expect(result.retryPolicy).not.toBeNull();
    expect(result.retryPolicy!.strategy).toBe('Fixed');
  });

  it('parses circuit breaker fixture', () => {
    const result = nodeBaseSchema.parse(cbFixture);
    expect(result.circuitBreakerPolicy).not.toBeNull();
    expect(result.circuitBreakerPolicy!.failureThreshold).toBe(50);
  });

  it('parses null policies fixture', () => {
    const result = nodeBaseSchema.parse(nullPoliciesFixture);
    expect(result.retryPolicy).toBeNull();
    expect(result.circuitBreakerPolicy).toBeNull();
  });

  it('fails on missing runtimeType', () => {
    const result = nodeBaseSchema.safeParse({ nodeId: 'n1' });
    expect(result.success).toBe(false);
  });

  it('fails on missing nodeId', () => {
    const result = nodeBaseSchema.safeParse({ runtimeType: 'Noop' });
    expect(result.success).toBe(false);
  });

  it('null retryPolicy stays null', () => {
    const input = { runtimeType: 'Noop', nodeId: 'n', retryPolicy: null };
    const parsed = nodeBaseSchema.parse(input);
    expect(parsed.retryPolicy).toBeNull();
  });

  it('undefined retryPolicy stays undefined', () => {
    const input = { runtimeType: 'Noop', nodeId: 'n' };
    const parsed = nodeBaseSchema.parse(input);
    expect(parsed.retryPolicy).toBeUndefined();
  });

  it('null circuitBreakerPolicy stays null', () => {
    const input = { runtimeType: 'Noop', nodeId: 'n', circuitBreakerPolicy: null };
    const parsed = nodeBaseSchema.parse(input);
    expect(parsed.circuitBreakerPolicy).toBeNull();
  });

  it('fails on maxConcurrentExecutions: -1', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      maxConcurrentExecutions: -1,
    });
    expect(result.success).toBe(false);
  });

  it('fails on maxConcurrentExecutions: 101', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      maxConcurrentExecutions: 101,
    });
    expect(result.success).toBe(false);
  });

  it('fails on retryPolicy maxAttempts: 11', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      retryPolicy: { strategy: 'Fixed', maxAttempts: 11 },
    });
    expect(result.success).toBe(false);
  });

  it('fails on circuitBreakerPolicy failureThreshold: 150', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      circuitBreakerPolicy: { failureThreshold: 150 },
    });
    expect(result.success).toBe(false);
  });

  it('configuration accepts nested arbitrary values', () => {
    const result = nodeBaseSchema.parse({
      runtimeType: 'Noop',
      nodeId: 'n',
      configuration: { key: 'value', nested: { a: [1, 2] }, flag: true },
    });
    expect(result.configuration).toEqual({ key: 'value', nested: { a: [1, 2] }, flag: true });
  });

  it('joinType defaults to Any', () => {
    const result = nodeBaseSchema.parse({ runtimeType: 'Noop', nodeId: 'n' });
    expect(result.joinType).toBe('Any');
  });

  it('priority defaults to Normal', () => {
    const result = nodeBaseSchema.parse({ runtimeType: 'Noop', nodeId: 'n' });
    expect(result.priority).toBe('Normal');
  });

  it('rejects unknown joinType', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      joinType: 'Either',
    });
    expect(result.success).toBe(false);
  });

  it('rejects unknown priority', () => {
    const result = nodeBaseSchema.safeParse({
      runtimeType: 'Noop',
      nodeId: 'n',
      priority: 'Critical',
    });
    expect(result.success).toBe(false);
  });

  it('has exactly 12 keys in shape', () => {
    expect(Object.keys(nodeBaseSchema.shape)).toHaveLength(12);
  });
});
