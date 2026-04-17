import fixture from '../__fixtures__/engine-enums.json';

describe('Enum parity (TS side)', () => {
  it('RuntimeType has 13 members', () => {
    expect(fixture.RuntimeType.members).toHaveLength(13);
  });

  it('RuntimeType members are valid string literals', () => {
    for (const member of fixture.RuntimeType.members) {
      expect(typeof member).toBe('string');
      expect(member.length).toBeGreaterThan(0);
    }
  });

  it('NodePriority has 3 members and no Critical', () => {
    expect(fixture.NodePriority.members).toHaveLength(3);
    expect(fixture.NodePriority.members).not.toContain('Critical');
  });

  it('NodePriority ordinals are 0, 1, 2', () => {
    expect(fixture.NodePriority.ordinals).toEqual([0, 1, 2]);
  });

  it('JoinType ordinals are 0, 1', () => {
    expect(fixture.JoinType.ordinals).toEqual([0, 1]);
  });

  it('ExecutionMode has Sequential and Parallel', () => {
    expect(fixture.ExecutionMode.members).toEqual(['Sequential', 'Parallel']);
  });

  it('MessageType has 5 members including Progress and Next', () => {
    expect(fixture.MessageType.members).toHaveLength(5);
    expect(fixture.MessageType.members).toContain('Progress');
    expect(fixture.MessageType.members).toContain('Next');
  });

  it('fixture covers exactly 7 enums', () => {
    expect(Object.keys(fixture)).toHaveLength(7);
  });
});
