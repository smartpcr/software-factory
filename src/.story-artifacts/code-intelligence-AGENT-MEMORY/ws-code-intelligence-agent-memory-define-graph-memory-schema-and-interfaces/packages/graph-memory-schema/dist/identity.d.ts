/**
 * Canonical identity generation for graph nodes.
 *
 * Every code node carries a canonical ID formed as:
 *   `{repo}:{language}:{qualified_name}:{content_hash_short}`
 *
 * The qualified name provides stability across commits; the content hash
 * disambiguates when signatures diverge. A reconciliation table maps old
 * IDs to current IDs after renames/moves (detected via git rename tracking
 * + AST signature matching).
 *
 * Examples:
 *   myrepo:typescript:src/utils/helpers.ts#MyClass:a1b2c3d4
 *   myrepo:typescript:src/utils/helpers.ts#MyClass.doWork:e5f6a7b8
 *   myrepo::src/utils:                (directory — no language, no hash)
 */
import type { NodeKind } from './types.js';
export interface CanonicalIdParts {
    repo: string;
    language: string;
    qualified_name: string;
    content_hash_short: string;
}
/**
 * Build a canonical node ID.
 *
 * The format is `repo:language:qualified_name:content_hash_short`.
 * Colons are reserved as field separators — `language`, `qualified_name`,
 * and `content_hash_short` must not contain literal colons.  The `repo`
 * field *may* contain colons (e.g. URLs) because the parser splits from
 * the right-hand side.
 *
 * @param repo               Repository identifier (name or URL; may contain colons).
 * @param language           Programming language (empty string for structural nodes). Must not contain colons.
 * @param qualified_name     Dot/hash-separated qualified name within the repo. Must not contain colons.
 * @param content_hash_short Short content hash for disambiguation (empty for containers). Must not contain colons.
 */
export declare function makeCanonicalId(repo: string, language: string, qualified_name: string, content_hash_short: string): string;
/**
 * Parse a canonical node ID into its four components. Returns null if malformed.
 *
 * The parser splits from the right: the last three colon-separated segments
 * are `content_hash_short`, `qualified_name`, and `language` (in reverse
 * order).  Everything before them is the `repo` (which may itself contain
 * colons).  This is unambiguous because {@link makeCanonicalId} guarantees
 * that `language`, `qualified_name`, and `content_hash_short` never contain
 * colons.
 */
export declare function parseCanonicalId(id: string): CanonicalIdParts | null;
/** Generate a simple node ID from kind and scope path (for structural nodes). */
export declare function makeNodeId(kind: NodeKind, scope_path: string): string;
/** Parse a simple kind:scope_path node ID. */
export declare function parseNodeId(id: string): {
    kind: string;
    scope_path: string;
} | null;
/**
 * Build a qualified name for a symbol contained within a file.
 *
 * @param file_path  Relative path of the file within the repo (e.g. "src/utils/helpers.ts").
 * @param parents    Enclosing symbol names, outermost first (e.g. ["MyClass"]).
 * @param name       The symbol's own name (e.g. "doWork").
 */
export declare function buildQualifiedName(file_path: string, parents: readonly string[], name: string): string;
/**
 * Build a file-level qualified name (for file, directory, module, package nodes).
 *
 * @param repo_id   The repo root identifier (URL or local name).
 * @param rel_path  Relative path within the repo.
 */
export declare function buildFileScopePath(repo_id: string, rel_path: string): string;
/** A single mapping from an old node ID to its current replacement. */
export interface ReconciliationEntry {
    old_id: string;
    new_id: string;
    reason: 'rename' | 'move' | 'signature_change' | 'merge';
    commit_sha: string;
    detected_at: string;
}
/**
 * Persistence contract for the reconciliation table.
 *
 * After renames/moves are detected (via git rename tracking + AST signature
 * matching), entries are recorded here so that edges and expertise referencing
 * old IDs can be transparently redirected.
 */
export interface ReconciliationTable {
    /** Record that old_id has been replaced by new_id. */
    record(entry: ReconciliationEntry): Promise<void>;
    /** Resolve a possibly-stale ID to its current ID, following chains. */
    resolve(id: string): Promise<string>;
    /** Return all entries recorded since a given commit. */
    entriesSince(commitSha: string): Promise<ReconciliationEntry[]>;
}
//# sourceMappingURL=identity.d.ts.map