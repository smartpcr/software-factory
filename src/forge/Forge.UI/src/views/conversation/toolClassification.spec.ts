// 2026-05-16 — unit tests for the Tools tab classification helper.
// The DOM-free `classifyTool` and arg extractors are easy to lock in;
// the React pane is exercised via the existing playwright e2e suite.

import { describe, expect, it } from 'vitest';
import {
  classifyTool,
  extractShellCommand,
  extractFilePath,
  basename,
} from './toolClassification';

describe('classifyTool', () => {
  it('returns mcp when McpServerName is non-null regardless of name', () => {
    expect(classifyTool('read_file', 'github')).toBe('mcp');
    expect(classifyTool('run_in_terminal', 'docker')).toBe('mcp');
    expect(classifyTool('', 'whatever')).toBe('mcp');
  });

  it('detects MCP via name conventions when McpServerName is absent', () => {
    expect(classifyTool('mcp_github_create_issue')).toBe('mcp');
    expect(classifyTool('github:create_issue')).toBe('mcp');
    // Colon in a URL fragment is NOT MCP — should fall through.
    expect(classifyTool('http://example.com')).not.toBe('mcp');
  });

  it('classifies shell tools by exact name match', () => {
    expect(classifyTool('run_in_terminal')).toBe('shell');
    expect(classifyTool('bash')).toBe('shell');
    expect(classifyTool('terminal')).toBe('shell');
    expect(classifyTool('execute')).toBe('shell');
    expect(classifyTool('shell')).toBe('shell');
    expect(classifyTool('cmd')).toBe('shell');
    // Copilot CLI on Windows ships the shell tool as `powershell` /
    // `pwsh`; historically these landed in "other" because the
    // whitelist only knew bash-family names. Lock the fix in.
    expect(classifyTool('powershell')).toBe('shell');
    expect(classifyTool('pwsh')).toBe('shell');
    expect(classifyTool('sh')).toBe('shell');
    expect(classifyTool('zsh')).toBe('shell');
    expect(classifyTool('run_command')).toBe('shell');
    // Case-insensitive.
    expect(classifyTool('Run_In_Terminal')).toBe('shell');
    expect(classifyTool('BASH')).toBe('shell');
    expect(classifyTool('PowerShell')).toBe('shell');
  });

  it('classifies file-write tools', () => {
    expect(classifyTool('create')).toBe('file-write');
    expect(classifyTool('write_file')).toBe('file-write');
    expect(classifyTool('edit')).toBe('file-write');
    expect(classifyTool('str_replace_editor')).toBe('file-write');
    expect(classifyTool('multi_edit')).toBe('file-write');
    expect(classifyTool('apply_patch')).toBe('file-write');
    expect(classifyTool('delete_file')).toBe('file-write');
    expect(classifyTool('rename')).toBe('file-write');
  });

  it('classifies file-read tools', () => {
    expect(classifyTool('read_file')).toBe('file-read');
    expect(classifyTool('view')).toBe('file-read');
    expect(classifyTool('cat')).toBe('file-read');
    expect(classifyTool('list_dir')).toBe('file-read');
    expect(classifyTool('ls')).toBe('file-read');
  });

  it('classifies search tools', () => {
    expect(classifyTool('grep')).toBe('search');
    // `rg` / `ripgrep` are the raw binary the Copilot SDK invokes on
    // Windows; historically misclassified as "other".
    expect(classifyTool('rg')).toBe('search');
    expect(classifyTool('ripgrep')).toBe('search');
    expect(classifyTool('find')).toBe('search');
    expect(classifyTool('glob')).toBe('search');
    expect(classifyTool('semantic_search')).toBe('search');
    expect(classifyTool('codebase_search_v2')).toBe('search');
  });

  it('classifies web tools', () => {
    expect(classifyTool('web_fetch')).toBe('web');
    expect(classifyTool('web_search')).toBe('web');
    expect(classifyTool('fetch')).toBe('web');
    expect(classifyTool('webfetch')).toBe('web');
    expect(classifyTool('browse')).toBe('web');
  });

  it('falls back to other for unknown names', () => {
    expect(classifyTool('do_the_thing')).toBe('other');
    expect(classifyTool('')).toBe('other');
    expect(classifyTool('unknown_tool_v3')).toBe('other');
  });
});

describe('extractShellCommand', () => {
  it('returns command from common arg shapes', () => {
    expect(extractShellCommand({ command: 'ls -la' })).toBe('ls -la');
    expect(extractShellCommand({ cmd: 'echo hi' })).toBe('echo hi');
    expect(extractShellCommand({ script: 'npm test' })).toBe('npm test');
    expect(extractShellCommand({ code: 'print(1)' })).toBe('print(1)');
  });

  it('passes through plain string args', () => {
    expect(extractShellCommand('git status')).toBe('git status');
  });

  it('returns null for unrecognized shapes', () => {
    expect(extractShellCommand(null)).toBeNull();
    expect(extractShellCommand({})).toBeNull();
    expect(extractShellCommand({ args: ['ls'] })).toBeNull();
    expect(extractShellCommand(42)).toBeNull();
  });
});

describe('extractFilePath', () => {
  it('returns path from common arg shapes', () => {
    expect(extractFilePath({ path: 'src/a.ts' })).toBe('src/a.ts');
    expect(extractFilePath({ file_path: 'src/b.ts' })).toBe('src/b.ts');
    expect(extractFilePath({ filePath: 'src/c.ts' })).toBe('src/c.ts');
    expect(extractFilePath({ file: 'src/d.ts' })).toBe('src/d.ts');
    expect(extractFilePath({ target: 'src/e.ts' })).toBe('src/e.ts');
    expect(extractFilePath({ absolute_path: '/abs/f.ts' })).toBe('/abs/f.ts');
  });

  it('returns null when no path-shaped key exists', () => {
    expect(extractFilePath(null)).toBeNull();
    expect(extractFilePath({ command: 'ls' })).toBeNull();
    expect(extractFilePath('just a string')).toBeNull();
  });
});

describe('basename', () => {
  it('handles forward and back slashes', () => {
    expect(basename('a/b/c.txt')).toBe('c.txt');
    expect(basename('C:\\users\\x\\file.cs')).toBe('file.cs');
    expect(basename('mixed/path\\winner.ts')).toBe('winner.ts');
  });

  it('returns input unchanged when no separator', () => {
    expect(basename('file.txt')).toBe('file.txt');
    expect(basename('')).toBe('');
  });
});
