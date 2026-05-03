import { describe, it, expect } from 'vitest';
import {
  encodeSegment,
  decodeSegment,
  repoIdFromUrl,
  buildNodeId,
  parseNodeId,
  buildEdgeId,
  isValidNodeId,
  isValidEdgeId,
  repositoryNodeId,
  fileNodeId,
  directoryNodeId,
  symbolNodeId,
  packageNodeId,
} from '../canonical-id.js';
import { NodeKind, EdgeKind } from '../types.js';

describe('canonical-id', () => {
  // ─── Segment encoding ───────────────────────────────────────

  describe('encodeSegment / decodeSegment', () => {
    it('passes through plain strings unchanged', () => {
      expect(encodeSegment('hello')).toBe('hello');
      expect(decodeSegment('hello')).toBe('hello');
    });

    it('encodes reserved characters', () => {
      expect(encodeSegment('a:b')).toBe('a%3Ab');
      expect(encodeSegment('a#b')).toBe('a%23b');
      expect(encodeSegment('a%b')).toBe('a%25b');
    });

    it('round-trips through encode/decode', () => {
      const cases = ['simple', 'with:colon', 'with#hash', 'with%percent', 'a:b#c%d'];
      for (const input of cases) {
        expect(decodeSegment(encodeSegment(input))).toBe(input);
      }
    });

    it('handles empty string', () => {
      expect(encodeSegment('')).toBe('');
      expect(decodeSegment('')).toBe('');
    });
  });

  // ─── Repo identity ─────────────────────────────────────────

  describe('repoIdFromUrl', () => {
    it('produces a 12-character hex string', () => {
      const id = repoIdFromUrl('https://github.com/org/repo');
      expect(id).toMatch(/^[0-9a-f]{12}$/);
    });

    it('is deterministic', () => {
      const url = 'https://github.com/org/repo';
      expect(repoIdFromUrl(url)).toBe(repoIdFromUrl(url));
    });

    it('normalizes trailing .git', () => {
      expect(repoIdFromUrl('https://github.com/org/repo.git'))
        .toBe(repoIdFromUrl('https://github.com/org/repo'));
    });

    it('normalizes trailing slashes', () => {
      expect(repoIdFromUrl('https://github.com/org/repo/'))
        .toBe(repoIdFromUrl('https://github.com/org/repo'));
    });

    it('is case-insensitive', () => {
      expect(repoIdFromUrl('https://GitHub.com/Org/Repo'))
        .toBe(repoIdFromUrl('https://github.com/org/repo'));
    });

    it('produces different IDs for different repos', () => {
      const id1 = repoIdFromUrl('https://github.com/org/repo-a');
      const id2 = repoIdFromUrl('https://github.com/org/repo-b');
      expect(id1).not.toBe(id2);
    });
  });

  // ─── Node ID construction ──────────────────────────────────

  describe('buildNodeId', () => {
    it('builds repository ID with kind prefix', () => {
      const id = buildNodeId({
        kind: NodeKind.Repository,
        repoId: 'abc123',
        pathSegments: [],
      });
      expect(id).toBe('repository:abc123');
    });

    it('builds file ID with path segments', () => {
      const id = buildNodeId({
        kind: NodeKind.File,
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
      });
      expect(id).toBe('file:abc123/src/auth/service.ts');
    });

    it('builds symbol ID with symbol chain', () => {
      const id = buildNodeId({
        kind: NodeKind.Function,
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: ['authenticate'],
      });
      expect(id).toBe('function:abc123/src/auth/service.ts#authenticate');
    });

    it('builds nested symbol ID (method in class)', () => {
      const id = buildNodeId({
        kind: NodeKind.Method,
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: ['AuthService', 'login'],
      });
      expect(id).toBe('method:abc123/src/auth/service.ts#AuthService#login');
    });

    it('encodes reserved characters in segments', () => {
      const id = buildNodeId({
        kind: NodeKind.File,
        repoId: 'abc123',
        pathSegments: ['src', 'file:with:colons.ts'],
      });
      expect(id).toBe('file:abc123/src/file%3Awith%3Acolons.ts');
    });
  });

  // ─── Node ID parsing ───────────────────────────────────────

  describe('parseNodeId', () => {
    it('parses repository ID', () => {
      const result = parseNodeId('repository:abc123');
      expect(result).toEqual({
        kind: 'repository',
        repoId: 'abc123',
        pathSegments: [],
        symbolChain: undefined,
      });
    });

    it('parses file ID', () => {
      const result = parseNodeId('file:abc123/src/auth/service.ts');
      expect(result).toEqual({
        kind: 'file',
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: undefined,
      });
    });

    it('parses function ID with symbol chain', () => {
      const result = parseNodeId('function:abc123/src/auth/service.ts#authenticate');
      expect(result).toEqual({
        kind: 'function',
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: ['authenticate'],
      });
    });

    it('parses method ID with nested symbol chain', () => {
      const result = parseNodeId('method:abc123/src/auth/service.ts#AuthService#login');
      expect(result).toEqual({
        kind: 'method',
        repoId: 'abc123',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: ['AuthService', 'login'],
      });
    });

    it('decodes encoded segments', () => {
      const result = parseNodeId('file:abc123/src/file%3Awith%3Acolons.ts');
      expect(result?.pathSegments).toEqual(['src', 'file:with:colons.ts']);
    });

    it('returns null for invalid kind', () => {
      expect(parseNodeId('unknown_kind:abc123')).toBeNull();
    });

    it('returns null for missing colon', () => {
      expect(parseNodeId('fileabc123')).toBeNull();
    });

    it('returns null for empty string after colon', () => {
      expect(parseNodeId('file:')).toBeNull();
    });

    it('round-trips through build/parse', () => {
      const components = {
        kind: NodeKind.Method as NodeKind,
        repoId: 'a1b2c3d4e5f6',
        pathSegments: ['src', 'auth', 'service.ts'],
        symbolChain: ['AuthService', 'login'],
      };
      const id = buildNodeId(components);
      const parsed = parseNodeId(id);
      expect(parsed).toEqual(components);
    });
  });

  // ─── Edge ID ────────────────────────────────────────────────

  describe('buildEdgeId', () => {
    it('produces a deterministic edge ID', () => {
      const source = 'function:abc123/src/a.ts#foo';
      const target = 'function:abc123/src/b.ts#bar';
      const id = buildEdgeId(source, target, EdgeKind.Calls);
      expect(id).toMatch(/^edge:[0-9a-f]{16}$/);
    });

    it('is deterministic — same inputs produce same ID', () => {
      const source = 'function:abc123/src/a.ts#foo';
      const target = 'function:abc123/src/b.ts#bar';
      expect(buildEdgeId(source, target, EdgeKind.Calls))
        .toBe(buildEdgeId(source, target, EdgeKind.Calls));
    });

    it('produces different IDs for different edge kinds', () => {
      const source = 'function:abc123/src/a.ts#foo';
      const target = 'function:abc123/src/b.ts#bar';
      expect(buildEdgeId(source, target, EdgeKind.Calls))
        .not.toBe(buildEdgeId(source, target, EdgeKind.References));
    });

    it('produces different IDs for swapped source/target', () => {
      const a = 'function:abc123/src/a.ts#foo';
      const b = 'function:abc123/src/b.ts#bar';
      expect(buildEdgeId(a, b, EdgeKind.Calls))
        .not.toBe(buildEdgeId(b, a, EdgeKind.Calls));
    });

    it('includes sourceSpan in hash when provided', () => {
      const source = 'function:abc123/src/a.ts#foo';
      const target = 'function:abc123/src/b.ts#bar';
      const span1 = { startLine: 10, startColumn: 5, endLine: 10, endColumn: 20 };
      const span2 = { startLine: 20, startColumn: 5, endLine: 20, endColumn: 20 };
      const id1 = buildEdgeId(source, target, EdgeKind.Calls, span1);
      const id2 = buildEdgeId(source, target, EdgeKind.Calls, span2);
      expect(id1).not.toBe(id2);
    });
  });

  // ─── ID validation ─────────────────────────────────────────

  describe('isValidNodeId', () => {
    it('returns true for valid node IDs', () => {
      expect(isValidNodeId('repository:abc123')).toBe(true);
      expect(isValidNodeId('file:abc123/src/main.ts')).toBe(true);
      expect(isValidNodeId('function:abc123/src/main.ts#main')).toBe(true);
    });

    it('returns false for edge IDs', () => {
      expect(isValidNodeId('edge:0123456789abcdef')).toBe(false);
    });

    it('returns false for garbage', () => {
      expect(isValidNodeId('')).toBe(false);
      expect(isValidNodeId('nope')).toBe(false);
    });
  });

  describe('isValidEdgeId', () => {
    it('returns true for valid edge IDs', () => {
      const id = buildEdgeId('a:x', 'b:y', EdgeKind.Calls);
      expect(isValidEdgeId(id)).toBe(true);
    });

    it('returns false for node IDs', () => {
      expect(isValidEdgeId('file:abc123/src/main.ts')).toBe(false);
    });

    it('returns false for garbage', () => {
      expect(isValidEdgeId('')).toBe(false);
      expect(isValidEdgeId('edge:short')).toBe(false);
    });
  });

  // ─── Convenience builders ──────────────────────────────────

  describe('convenience builders', () => {
    const repoId = 'a1b2c3d4e5f6';

    it('repositoryNodeId', () => {
      expect(repositoryNodeId(repoId)).toBe('repository:a1b2c3d4e5f6');
    });

    it('fileNodeId', () => {
      expect(fileNodeId(repoId, 'src/auth/service.ts'))
        .toBe('file:a1b2c3d4e5f6/src/auth/service.ts');
    });

    it('fileNodeId normalizes backslashes', () => {
      expect(fileNodeId(repoId, 'src\\auth\\service.ts'))
        .toBe('file:a1b2c3d4e5f6/src/auth/service.ts');
    });

    it('directoryNodeId', () => {
      expect(directoryNodeId(repoId, 'src/auth'))
        .toBe('directory:a1b2c3d4e5f6/src/auth');
    });

    it('symbolNodeId', () => {
      expect(symbolNodeId(NodeKind.Function, repoId, 'src/auth/service.ts', ['authenticate']))
        .toBe('function:a1b2c3d4e5f6/src/auth/service.ts#authenticate');
    });

    it('symbolNodeId with nested chain', () => {
      expect(symbolNodeId(NodeKind.Method, repoId, 'src/auth/service.ts', ['AuthService', 'login']))
        .toBe('method:a1b2c3d4e5f6/src/auth/service.ts#AuthService#login');
    });

    it('packageNodeId', () => {
      expect(packageNodeId(repoId, 'packages/auth'))
        .toBe('package:a1b2c3d4e5f6/packages/auth');
    });
  });
});
