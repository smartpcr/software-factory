// 2026-05-16 — pure-function helper for classifying agent tool calls into
// operator-meaningful buckets. Used by ToolsPane to render category
// summaries + filter chips. Kept separate from the pane so the
// classification logic is unit-testable without DOM dependencies.

/**
 * Operator-meaningful tool buckets. Aligned with the user-selected
 * categories (file-read / file-write / shell / search / web / mcp /
 * other). The ordering here is the canonical render order on the
 * summary strip; keep it stable.
 */
export type ToolCategory =
  | 'file-read'
  | 'file-write'
  | 'shell'
  | 'search'
  | 'web'
  | 'mcp'
  | 'other';

export const TOOL_CATEGORIES: ToolCategory[] = [
  'file-read',
  'file-write',
  'shell',
  'search',
  'web',
  'mcp',
  'other',
];

/**
 * Compact display labels for each category — what shows in the strip
 * counts and filter chips. ASCII-leaning so it survives in the
 * terminal-style swarm-control theme without forcing the operator's
 * font stack to render emoji.
 */
export const CATEGORY_LABELS: Record<ToolCategory, string> = {
  'file-read':  'file-read',
  'file-write': 'file-write',
  'shell':      'shell',
  'search':     'search',
  'web':        'web',
  'mcp':        'mcp',
  'other':      'other',
};

/**
 * One-character glyph per category — rendered as a leading badge on
 * each tool card. ASCII-only so the page renders without an emoji
 * font (operators run swarm-control in raw terminal-style mode).
 */
export const CATEGORY_GLYPHS: Record<ToolCategory, string> = {
  'file-read':  'R',
  'file-write': 'W',
  'shell':      '$',
  'search':     '/',
  'web':        '@',
  'mcp':        '+',
  'other':      '·',
};

/**
 * Tone color per category — drives the left-border accent on cards
 * and the category strip count badges. Maps to the `--sc-*` theme
 * vars used by sibling panes (BuildPane, WorktreePane) so the look
 * is consistent across the conversation view.
 */
export const CATEGORY_TONES: Record<ToolCategory, { var: string; fallback: string }> = {
  'file-read':  { var: '--sc-blue,   #60a5fa', fallback: '#60a5fa' },
  'file-write': { var: '--sc-cyan,   #22d3ee', fallback: '#22d3ee' },
  'shell':      { var: '--sc-amber,  #fbbf24', fallback: '#fbbf24' },
  'search':     { var: '--sc-purple, #c084fc', fallback: '#c084fc' },
  'web':        { var: '--sc-green,  #34d399', fallback: '#34d399' },
  'mcp':        { var: '--sc-pink,   #f472b6', fallback: '#f472b6' },
  'other':      { var: '--sc-text-dim, #9ca3af', fallback: '#9ca3af' },
};

/**
 * Classify a tool by its name (and optional MCP server hint). The
 * order of checks matters: MCP must win over name-based heuristics
 * since an MCP server can expose ANY tool name and the operator's
 * mental model is "all my MCP traffic, one group".
 *
 * @param toolName Tool name from the SDK's <c>ToolExecutionStartData.ToolName</c>
 *   (e.g. "read_file", "run_in_terminal", "github:create_issue").
 * @param mcpServerName Non-null when the tool was dispatched through an
 *   MCP server (SDK's <c>ToolExecutionStartData.McpServerName</c>). Phase 2
 *   backend enrichment populates this; falls back to a name-prefix sniff
 *   ("mcp_" / "{server}:") for events emitted before the backend lands.
 */
export function classifyTool(toolName: string, mcpServerName?: string | null): ToolCategory {
  if (mcpServerName) return 'mcp';

  // No backend ToolName? Default conservatively.
  if (!toolName) return 'other';
  const n = toolName.toLowerCase();

  // MCP name conventions — both "mcp_<server>_<tool>" (Copilot CLI
  // convention) and "<server>:<tool>" (some MCP frameworks). Caught
  // here too so the classification works on legacy events with no
  // explicit McpServerName.
  if (n.startsWith('mcp_') || n.includes(':') && !n.startsWith('http')) return 'mcp';

  // Shell — `run_in_terminal` is the Copilot SDK name; `bash` and
  // `terminal` cover Claude CLI / generic backends; `powershell` /
  // `pwsh` are the Copilot-CLI shell tool on Windows (the actual name
  // emitted by the SDK on this host — historically missing from the
  // whitelist, which is why the operator saw a huge "other" bucket).
  // `execute` is sometimes used by sandboxed runners.
  if (n === 'run_in_terminal'
    || n === 'bash'
    || n === 'terminal'
    || n === 'execute'
    || n === 'shell'
    || n === 'cmd'
    || n === 'powershell'
    || n === 'pwsh'
    || n === 'sh'
    || n === 'zsh'
    || n === 'run_command') {
    return 'shell';
  }

  // Web — fetch + search variants.
  if (n.startsWith('web_')
    || n === 'fetch'
    || n === 'webfetch'
    || n === 'browse'
    || n === 'browser') {
    return 'web';
  }

  // Search — grep / find / semantic search. Distinguish from file-read
  // because operators frequently want to know "how many greps did the
  // agent run?" as a discovery-vs-edit ratio. `rg` / `ripgrep` are the
  // raw binary the Copilot SDK invokes on Windows; historically these
  // landed in "other" alongside `powershell`.
  if (n === 'grep'
    || n === 'rg'
    || n === 'ripgrep'
    || n === 'find'
    || n === 'search'
    || n === 'glob' // contested — many ops treat glob as listing rather than search
    || n.includes('semantic_search')
    || n.includes('codebase_search')) {
    return 'search';
  }

  // File-write — anything that mutates a file or directory.
  if (n === 'create'
    || n === 'create_file'
    || n === 'write'
    || n === 'write_file'
    || n === 'edit'
    || n === 'edit_file'
    || n === 'str_replace_editor' // single tool that does both read+write modes
    || n === 'multi_edit'
    || n === 'apply_patch'
    || n === 'delete'
    || n === 'delete_file'
    || n === 'remove'
    || n === 'rename'
    || n === 'move') {
    return 'file-write';
  }

  // File-read — read + view + list operations.
  if (n === 'read'
    || n === 'read_file'
    || n === 'view'
    || n === 'cat'
    || n === 'ls'
    || n === 'list'
    || n === 'list_dir'
    || n === 'stat') {
    return 'file-read';
  }

  return 'other';
}

/**
 * Heuristic command extractor for shell tool-call arguments. Different
 * runner backends nest the actual command line at different paths in
 * the arguments JSON. We try the most common shapes; falls back to a
 * pretty-printed JSON view when nothing matches so the operator at
 * least sees the raw payload.
 */
export function extractShellCommand(args: unknown): string | null {
  if (args == null) return null;
  if (typeof args === 'string') return args;
  if (typeof args !== 'object' || Array.isArray(args)) return null;
  const a = args as Record<string, unknown>;
  // Copilot SDK uses `command`. Claude tools use `command` too but
  // some legacy paths put it in `cmd` or `script`.
  for (const k of ['command', 'cmd', 'script', 'code']) {
    if (typeof a[k] === 'string') return a[k] as string;
  }
  return null;
}

/**
 * Heuristic path extractor for file-read / file-write tool arguments.
 * Pulled out so the card header can show "src/forge/.../X.cs" without
 * forcing the operator to expand the args. Returns null when no
 * obvious path key is present (e.g. semantic_search whose args don't
 * include a path).
 */
export function extractFilePath(args: unknown): string | null {
  if (args == null) return null;
  if (typeof args !== 'object' || Array.isArray(args)) return null;
  const a = args as Record<string, unknown>;
  for (const k of ['path', 'file_path', 'filepath', 'filePath', 'file', 'target', 'absolute_path']) {
    if (typeof a[k] === 'string') return a[k] as string;
  }
  return null;
}

/**
 * Compute the basename of a full file path. Used when the path is
 * long enough that the card header would line-wrap. Keeps the slash
 * style of the input so Windows backslashes survive ("Forge.Api.cs").
 */
export function basename(path: string): string {
  if (!path) return path;
  const ix = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  return ix < 0 ? path : path.substring(ix + 1);
}
