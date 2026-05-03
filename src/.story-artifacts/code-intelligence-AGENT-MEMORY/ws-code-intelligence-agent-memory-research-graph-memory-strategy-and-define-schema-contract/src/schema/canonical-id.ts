/**
 * Canonical ID generation and parsing for graph nodes and edges.
 *
 * ## ID Grammar
 *
 * Node IDs follow this format:
 *   `{kind}:{repo_id}/{path_or_qualified_name}`
 *
 * Where:
 *   - `kind` is one of the NodeKind enum values (e.g., "function", "file")
 *   - `repo_id` is a stable repository identifier (URL hash or user-assigned)
 *   - path segments are separated by `/`
 *   - symbol names within a file use `#` as separator:
 *     `function:repo123/src/auth/service.ts#AuthService#authenticate`
 *
 * Reserved characters (`:`, `/`, `#`) within individual segments are
 * percent-encoded using the same rules as URI encoding.
 *
 * Edge IDs are deterministic FNV-1a hashes of `(sourceId, targetId, kind)`.
 * This ensures that the same relationship always produces the same edge ID,
 * enabling idempotent upserts.
 *
 * ## Repo Identity
 *
 * The `repo_id` segment uses a truncated FNV-1a hash (64-bit, rendered as
 * 16 hex chars, then truncated to 12) of the repo's normalized clone URL
 * (or an explicit user-assigned identifier). This provides:
 * - Immutability: renaming a repo doesn't change existing IDs
 * - Compactness: 12-char hex is unique enough for local use
 * - Determinism: same URL always produces the same ID
 */

import { NodeKind, NODE_KINDS, EdgeKind, type SourceSpan } from './types.js';

// ─── Encoding / decoding ─────────────────────────────────────────

const RESERVED_CHARS = /[:%#]/g;
const ENCODED_PATTERN = /%(25|3A|23)/g;

const ENCODE_MAP: Record<string, string> = {
  ':': '%3A',
  '%': '%25',
  '#': '%23',
};

const DECODE_MAP: Record<string, string> = {
  '%3A': ':',
  '%25': '%',
  '%23': '#',
};

/** Percent-encode reserved characters within a single ID segment. */
export function encodeSegment(segment: string): string {
  // Encode % first to avoid double-encoding
  return segment.replace(/%/g, '%25').replace(/[:#]/g, (ch) => ENCODE_MAP[ch]!);
}

/** Decode a percent-encoded segment back to its original value. */
export function decodeSegment(encoded: string): string {
  return encoded.replace(ENCODED_PATTERN, (token) => DECODE_MAP[token] ?? token);
}

// ─── Repo identity ───────────────────────────────────────────────

/**
 * Generate a stable repo identifier from a clone URL.
 * Uses a simple hash truncated to 12 hex characters.
 */
export function repoIdFromUrl(url: string): string {
  return hashString(normalizeRepoUrl(url)).slice(0, 12);
}

/**
 * Normalize a git remote URL for consistent hashing.
 * Strips trailing `.git`, trailing slashes, and lowercases.
 */
function normalizeRepoUrl(url: string): string {
  return url
    .trim()
    .toLowerCase()
    .replace(/\.git\s*$/, '')
    .replace(/\/+$/, '');
}

/**
 * Simple deterministic string hash producing a hex digest.
 * Uses FNV-1a 64-bit for speed; not cryptographic.
 */
function hashString(input: string): string {
  // FNV-1a 64-bit implemented as two 32-bit halves
  let h1 = 0x811c9dc5;
  let h2 = 0xcbf29ce4;
  for (let i = 0; i < input.length; i++) {
    const c = input.charCodeAt(i);
    h1 = Math.imul(h1 ^ c, 0x01000193);
    h2 = Math.imul(h2 ^ c, 0x01000193);
  }
  return (
    (h1 >>> 0).toString(16).padStart(8, '0') +
    (h2 >>> 0).toString(16).padStart(8, '0')
  );
}

// ─── Node ID construction ────────────────────────────────────────

export interface NodeIdComponents {
  readonly kind: NodeKind;
  readonly repoId: string;
  /** Path segments (e.g., directory path parts, file path). */
  readonly pathSegments: readonly string[];
  /** Symbol chain within a file (e.g., class name, method name). */
  readonly symbolChain?: readonly string[];
}

/**
 * Build a canonical node ID from its components.
 *
 * Examples:
 *   repository node: `repository:a1b2c3d4e5f6`
 *   file node:       `file:a1b2c3d4e5f6/src/auth/service.ts`
 *   function node:   `function:a1b2c3d4e5f6/src/auth/service.ts#authenticate`
 *   method node:     `method:a1b2c3d4e5f6/src/auth/service.ts#AuthService#login`
 */
export function buildNodeId(components: NodeIdComponents): string {
  const { kind, repoId, pathSegments, symbolChain } = components;

  let id = `${kind}:${encodeSegment(repoId)}`;

  if (pathSegments.length > 0) {
    id += '/' + pathSegments.map(encodeSegment).join('/');
  }

  if (symbolChain && symbolChain.length > 0) {
    id += '#' + symbolChain.map(encodeSegment).join('#');
  }

  return id;
}

/**
 * Parse a canonical node ID back into its components.
 * Returns null if the ID does not match the expected grammar.
 */
export function parseNodeId(id: string): NodeIdComponents | null {
  // Split kind from the rest: "kind:rest"
  const colonIdx = id.indexOf(':');
  if (colonIdx < 0) return null;

  const kind = id.slice(0, colonIdx);
  if (!NODE_KINDS.includes(kind as NodeKind)) return null;

  const rest = id.slice(colonIdx + 1);
  if (rest.length === 0) return null;

  // Split path from symbol chain
  const hashIdx = rest.indexOf('#');
  let pathPart: string;
  let symbolPart: string | undefined;

  if (hashIdx >= 0) {
    pathPart = rest.slice(0, hashIdx);
    symbolPart = rest.slice(hashIdx + 1);
  } else {
    pathPart = rest;
  }

  // First path segment is always the repo ID
  const pathSegmentsRaw = pathPart.split('/');
  const repoId = decodeSegment(pathSegmentsRaw[0]!);
  const pathSegments = pathSegmentsRaw.slice(1).map(decodeSegment);

  const symbolChain = symbolPart
    ? symbolPart.split('#').map(decodeSegment)
    : undefined;

  return {
    kind: kind as NodeKind,
    repoId,
    pathSegments,
    symbolChain: symbolChain && symbolChain.length > 0 ? symbolChain : undefined,
  };
}

// ─── Edge ID construction ────────────────────────────────────────

/**
 * Generate a deterministic edge ID from its constituent parts.
 * The ID is a hash of (sourceId, targetId, kind) — ensuring that
 * the same relationship always maps to the same edge regardless
 * of extraction order.
 *
 * If a sourceSpan is provided, it is included in the hash to allow
 * multiple edges of the same kind between the same pair of nodes
 * (e.g., multiple call sites).
 */
export function buildEdgeId(
  sourceId: string,
  targetId: string,
  kind: EdgeKind,
  sourceSpan?: SourceSpan,
): string {
  let input = `${sourceId}\0${targetId}\0${kind}`;
  if (sourceSpan) {
    input += `\0${sourceSpan.startLine}:${sourceSpan.startColumn}`;
  }
  return `edge:${hashString(input)}`;
}

/**
 * Check whether a string looks like a valid canonical node ID.
 * Does not validate that the referenced node exists.
 */
export function isValidNodeId(id: string): boolean {
  return parseNodeId(id) !== null;
}

/**
 * Check whether a string looks like a valid edge ID.
 */
export function isValidEdgeId(id: string): boolean {
  return /^edge:[0-9a-f]{16}$/.test(id);
}

// ─── Convenience builders ────────────────────────────────────────

/** Build a repository node ID. */
export function repositoryNodeId(repoId: string): string {
  return buildNodeId({ kind: NodeKind.Repository, repoId, pathSegments: [] });
}

/** Build a file node ID from its repo-relative path. */
export function fileNodeId(repoId: string, filePath: string): string {
  // Normalize path separators to forward slashes
  const segments = filePath.replace(/\\/g, '/').split('/').filter(Boolean);
  return buildNodeId({ kind: NodeKind.File, repoId, pathSegments: segments });
}

/** Build a directory node ID. */
export function directoryNodeId(repoId: string, dirPath: string): string {
  const segments = dirPath.replace(/\\/g, '/').split('/').filter(Boolean);
  return buildNodeId({ kind: NodeKind.Directory, repoId, pathSegments: segments });
}

/** Build a symbol node ID (function, class, method, etc.). */
export function symbolNodeId(
  kind: NodeKind,
  repoId: string,
  filePath: string,
  symbolChain: readonly string[],
): string {
  const pathSegments = filePath.replace(/\\/g, '/').split('/').filter(Boolean);
  return buildNodeId({ kind, repoId, pathSegments, symbolChain });
}

/** Build a package node ID. */
export function packageNodeId(repoId: string, packagePath: string): string {
  const segments = packagePath.replace(/\\/g, '/').split('/').filter(Boolean);
  return buildNodeId({ kind: NodeKind.Package, repoId, pathSegments: segments });
}
