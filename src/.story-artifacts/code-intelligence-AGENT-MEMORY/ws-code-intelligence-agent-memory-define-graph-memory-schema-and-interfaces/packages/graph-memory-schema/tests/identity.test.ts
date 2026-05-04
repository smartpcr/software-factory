/**
 * Tests for canonical identity model.
 */
import { describe, it, expect } from 'vitest';
import {
  makeCanonicalId,
  parseCanonicalId,
  makeNodeId,
  parseNodeId,
  buildQualifiedName,
  buildFileScopePath,
} from '../src/identity.js';

describe('makeCanonicalId', () => {
  it('builds repo:language:qualified_name:hash format', () => {
    const id = makeCanonicalId('myrepo', 'typescript', 'src/utils/helpers.ts#MyClass', 'a1b2c3d4');
    expect(id).toBe('myrepo:typescript:src/utils/helpers.ts#MyClass:a1b2c3d4');
  });

  it('supports empty language for structural nodes', () => {
    const id = makeCanonicalId('myrepo', '', 'src/utils', '');
    expect(id).toBe('myrepo::src/utils:');
  });

  it('throws on empty repo', () => {
    expect(() => makeCanonicalId('', 'ts', 'foo', 'abc')).toThrow('repo must be a non-empty string');
  });

  it('throws on empty qualified_name', () => {
    expect(() => makeCanonicalId('repo', 'ts', '', 'abc')).toThrow('qualified_name must be a non-empty string');
  });
});

describe('parseCanonicalId', () => {
  it('round-trips a canonical ID', () => {
    const id = 'myrepo:typescript:src/helpers.ts#Foo:a1b2c3d4';
    const parsed = parseCanonicalId(id);
    expect(parsed).toEqual({
      repo: 'myrepo',
      language: 'typescript',
      qualified_name: 'src/helpers.ts#Foo',
      content_hash_short: 'a1b2c3d4',
    });
  });

  it('handles empty language and hash', () => {
    const parsed = parseCanonicalId('myrepo::src/utils:');
    expect(parsed).toEqual({
      repo: 'myrepo',
      language: '',
      qualified_name: 'src/utils',
      content_hash_short: '',
    });
  });

  it('returns null for malformed ID with < 4 parts', () => {
    expect(parseCanonicalId('foo:bar')).toBeNull();
    expect(parseCanonicalId('single')).toBeNull();
  });
});

describe('makeNodeId / parseNodeId (legacy simple ID)', () => {
  it('builds kind:scope_path', () => {
    expect(makeNodeId('file', 'src/main.ts')).toBe('file:src/main.ts');
  });

  it('throws on empty scope_path', () => {
    expect(() => makeNodeId('repo', '')).toThrow();
  });

  it('round-trips correctly', () => {
    const id = makeNodeId('class', 'repo/src/A.ts#MyClass');
    const parsed = parseNodeId(id);
    expect(parsed).toEqual({ kind: 'class', scope_path: 'repo/src/A.ts#MyClass' });
  });

  it('returns null for invalid format', () => {
    expect(parseNodeId('nocolon')).toBeNull();
  });
});

describe('buildQualifiedName', () => {
  it('builds file#Parent.Child format', () => {
    expect(buildQualifiedName('src/a.ts', ['MyClass'], 'doWork'))
      .toBe('src/a.ts#MyClass.doWork');
  });

  it('handles no parents', () => {
    expect(buildQualifiedName('src/a.ts', [], 'foo'))
      .toBe('src/a.ts#foo');
  });

  it('handles deeply nested', () => {
    expect(buildQualifiedName('f.ts', ['A', 'B', 'C'], 'D'))
      .toBe('f.ts#A.B.C.D');
  });
});

describe('buildFileScopePath', () => {
  it('joins repo and path', () => {
    expect(buildFileScopePath('myrepo', 'src/utils')).toBe('myrepo/src/utils');
  });

  it('normalises backslashes', () => {
    expect(buildFileScopePath('repo', 'src\\util\\foo')).toBe('repo/src/util/foo');
  });

  it('strips trailing slashes', () => {
    expect(buildFileScopePath('repo/', 'src/')).toBe('repo/src');
  });

  it('handles empty rel_path', () => {
    expect(buildFileScopePath('myrepo', '')).toBe('myrepo');
  });
});
