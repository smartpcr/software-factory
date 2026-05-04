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
export function makeCanonicalId(repo, language, qualified_name, content_hash_short) {
    if (!repo)
        throw new Error('repo must be a non-empty string');
    if (!qualified_name)
        throw new Error('qualified_name must be a non-empty string');
    if (language.includes(':'))
        throw new Error('language must not contain colons; colons are reserved as field separators');
    if (qualified_name.includes(':'))
        throw new Error('qualified_name must not contain colons; use # and . as hierarchy separators');
    if (content_hash_short.includes(':'))
        throw new Error('content_hash_short must not contain colons');
    return `${repo}:${language}:${qualified_name}:${content_hash_short}`;
}
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
export function parseCanonicalId(id) {
    const parts = id.split(':');
    if (parts.length < 4)
        return null;
    const content_hash_short = parts[parts.length - 1];
    const qualified_name = parts[parts.length - 2];
    const language = parts[parts.length - 3];
    const repo = parts.slice(0, parts.length - 3).join(':');
    if (!repo)
        return null;
    if (!qualified_name)
        return null;
    return { repo, language, qualified_name, content_hash_short };
}
// ─── Legacy simple ID helpers (for structural nodes) ─────────────────────
/** Generate a simple node ID from kind and scope path (for structural nodes). */
export function makeNodeId(kind, scope_path) {
    if (!scope_path) {
        throw new Error('scope_path must be a non-empty string');
    }
    return `${kind}:${scope_path}`;
}
/** Parse a simple kind:scope_path node ID. */
export function parseNodeId(id) {
    const colon = id.indexOf(':');
    if (colon < 1)
        return null;
    return {
        kind: id.substring(0, colon),
        scope_path: id.substring(colon + 1),
    };
}
// ─── Qualified name construction ─────────────────────────────────────────
/**
 * Build a qualified name for a symbol contained within a file.
 *
 * @param file_path  Relative path of the file within the repo (e.g. "src/utils/helpers.ts").
 * @param parents    Enclosing symbol names, outermost first (e.g. ["MyClass"]).
 * @param name       The symbol's own name (e.g. "doWork").
 */
export function buildQualifiedName(file_path, parents, name) {
    const chain = [...parents, name].join('.');
    return `${file_path}#${chain}`;
}
/**
 * Build a file-level qualified name (for file, directory, module, package nodes).
 *
 * @param repo_id   The repo root identifier (URL or local name).
 * @param rel_path  Relative path within the repo.
 */
export function buildFileScopePath(repo_id, rel_path) {
    const normalised = rel_path.replace(/\\/g, '/').replace(/\/+$/, '');
    const root = repo_id.replace(/\/+$/, '');
    return normalised ? `${root}/${normalised}` : root;
}
//# sourceMappingURL=identity.js.map