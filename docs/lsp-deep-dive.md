# LSP Deep Dive: Language Intelligence in OpenCode

> Last updated: 2026-01-22

This document explains how OpenCode leverages the Language Server Protocol (LSP) for code intelligence, and why this approach is superior to AST-based parsing for repository understanding.

---

## Table of Contents

1. [What is LSP?](#what-is-lsp)
2. [LSP vs AST-Based Approaches](#lsp-vs-ast-based-approaches)
3. [How OpenCode Uses LSP](#how-opencode-uses-lsp)
4. [Architecture](#architecture)
5. [Supported Language Servers](#supported-language-servers)
6. [LSP Operations](#lsp-operations)
7. [Integration with AI Tools](#integration-with-ai-tools)
8. [Configuration](#configuration)

---

## What is LSP?

The **Language Server Protocol (LSP)** is an open, JSON-RPC based protocol that standardizes communication between code editors/tools and language servers. Originally created by Microsoft for VS Code, it's now an industry standard supported by virtually every major IDE and editor.

### Key Concepts

```
┌─────────────────┐                    ┌──────────────────┐
│    Client       │  JSON-RPC/stdio    │  Language        │
│  (OpenCode)     │◄──────────────────►│  Server          │
│                 │                    │  (e.g., tsserver)│
└─────────────────┘                    └──────────────────┘
        │                                      │
        │ Requests:                            │ Provides:
        │ - textDocument/hover                 │ - Type info
        │ - textDocument/definition            │ - Diagnostics
        │ - textDocument/references            │ - Navigation
        │ - workspace/symbol                   │ - Completions
        └──────────────────────────────────────┘
```

### Protocol Messages

| Category | Examples |
|----------|----------|
| **Lifecycle** | `initialize`, `initialized`, `shutdown` |
| **Diagnostics** | `textDocument/publishDiagnostics` |
| **Navigation** | `textDocument/definition`, `textDocument/references`, `textDocument/implementation` |
| **Information** | `textDocument/hover`, `textDocument/documentSymbol`, `workspace/symbol` |
| **Refactoring** | `textDocument/rename`, `textDocument/codeAction` |

---

## LSP vs AST-Based Approaches

### Traditional AST Parsing

Many AI coding tools use direct AST (Abstract Syntax Tree) parsing:

```
Source Code → Parser → AST → Analysis
```

**Limitations of AST-only approach:**

1. **No Type Resolution**: ASTs represent syntax, not semantics
   ```typescript
   const result = getValue()  // AST knows it's a function call
                              // But doesn't know the return type
   ```

2. **No Cross-File Understanding**: Each file parsed in isolation
   ```typescript
   import { User } from './models'
   const user: User = ...  // AST doesn't resolve User definition
   ```

3. **No Project Context**: Missing build config, tsconfig.json, etc.
   ```typescript
   // Path aliases like @/components won't resolve
   import { Button } from '@/components/button'
   ```

4. **Language-Specific Parsers**: Need different parsers for each language

5. **Incomplete Analysis**: Can't detect type errors, unused imports, etc.

### LSP Advantage

Language servers provide **semantic analysis**, not just syntax:

```
Source Code → Language Server → Rich Semantic Information
                    │
                    ├── Type inference
                    ├── Cross-file resolution
                    ├── Project-aware analysis
                    ├── Error detection
                    └── Build system integration
```

**Benefits:**

| Feature | AST-Only | LSP |
|---------|----------|-----|
| Type information | ❌ | ✅ |
| Cross-file navigation | ❌ | ✅ |
| Import resolution | ❌ | ✅ |
| Path alias support | ❌ | ✅ |
| Error diagnostics | Limited | ✅ Full |
| Refactoring support | ❌ | ✅ |
| Build config awareness | ❌ | ✅ |
| Language coverage | Per-parser | Universal |

### Real-World Example

Consider understanding this TypeScript code:

```typescript
// user.service.ts
import { Injectable } from '@nestjs/common'
import { InjectRepository } from '@nestjs/typeorm'
import { Repository } from 'typeorm'
import { User } from './entities/user.entity'

@Injectable()
export class UserService {
  constructor(
    @InjectRepository(User)
    private readonly userRepository: Repository<User>
  ) {}

  async findByEmail(email: string): Promise<User | null> {
    return this.userRepository.findOne({ where: { email } })
  }
}
```

**AST-only approach knows:**
- There's a class `UserService`
- It has a method `findByEmail`
- There are some decorators and imports

**LSP approach knows:**
- `User` is an entity class defined in `./entities/user.entity.ts`
- `Repository<User>` is a generic type from TypeORM
- `findOne` returns `Promise<User | null>`
- The decorators come from NestJS with specific semantics
- Any type errors in the code
- All references to this class across the project

---

## How OpenCode Uses LSP

### Automatic Server Management

OpenCode automatically:

1. **Detects project type** from lock files (package.json, Cargo.toml, etc.)
2. **Spawns appropriate language servers** for detected languages
3. **Manages server lifecycle** (start, restart, shutdown)
4. **Routes requests** to correct server based on file extension

### Integration Points

```
┌─────────────────────────────────────────────────────────┐
│                    OpenCode Session                     │
│                                                         │
│  ┌─────────────┐     ┌─────────────┐     ┌──────────┐   │
│  │   AI Agent  │────►│  LSP Module │────►│ Language │   │
│  │             │     │             │     │ Servers  │   │
│  └─────────────┘     └─────────────┘     └──────────┘   │
│        │                    │                  │        │
│        │                    │                  │        │
│   Tool Calls           Queries:           Provides:     │
│   - read file         - diagnostics       - Type info   │
│   - edit file         - hover info        - Errors      │
│   - find refs         - definitions       - Symbols     │
│                       - references                      │
└─────────────────────────────────────────────────────────┘
```

### Key Use Cases

1. **Pre-Edit Validation**: Check diagnostics before making changes
2. **Post-Edit Verification**: Confirm changes don't introduce errors
3. **Symbol Navigation**: Find definitions and references
4. **Code Understanding**: Get hover documentation and type info
5. **Project Analysis**: List symbols across workspace

---

## Architecture

### Component Overview

```
packages/opencode/src/lsp/
├── index.ts      # Main LSP namespace - coordinates everything
├── server.ts     # Language server definitions (20+ servers)
├── client.ts     # LSP client implementation (JSON-RPC)
└── language.ts   # File extension → language ID mapping
```

### Server Discovery Flow

```typescript
// 1. Detect project type from file
const root = await server.root(file)  // Finds nearest package.json, Cargo.toml, etc.

// 2. Check if server already running for this root
const existing = clients.find(c => c.root === root && c.serverID === server.id)

// 3. Spawn new server if needed
const handle = await server.spawn(root)
const client = await LSPClient.create({ serverID, server: handle, root })
```

### Client-Server Communication

```typescript
// Create JSON-RPC connection over stdio
const connection = createMessageConnection(
  new StreamMessageReader(process.stdout),
  new StreamMessageWriter(process.stdin)
)

// Initialize server with workspace info
await connection.sendRequest("initialize", {
  rootUri: pathToFileURL(root).href,
  workspaceFolders: [{ name: "workspace", uri: rootUri }],
  capabilities: {
    textDocument: {
      synchronization: { didOpen: true, didChange: true },
      publishDiagnostics: { versionSupport: true },
    },
    workspace: {
      configuration: true,
      didChangeWatchedFiles: { dynamicRegistration: true },
    },
  },
})
```

### Diagnostics Flow

```
File Change → touchFile() → didOpen/didChange notification
                                      │
                                      ▼
                            Language Server analyzes
                                      │
                                      ▼
                         publishDiagnostics notification
                                      │
                                      ▼
                          OpenCode stores diagnostics
                                      │
                                      ▼
                    AI can query LSP.diagnostics() for errors
```

---

## Supported Language Servers

### Built-in Servers

| Language | Server | Root Detection | Extensions |
|----------|--------|----------------|------------|
| TypeScript/JS | `typescript-language-server` | `package.json`, lockfiles | `.ts`, `.tsx`, `.js`, `.jsx`, `.mjs` |
| Python | `pyright` | `pyproject.toml`, `requirements.txt` | `.py` |
| Rust | `rust-analyzer` | `Cargo.toml` | `.rs` |
| Go | `gopls` | `go.mod` | `.go` |
| C/C++ | `clangd` | `compile_commands.json`, `CMakeLists.txt` | `.c`, `.cpp`, `.h`, `.hpp` |
| Java | `jdtls` | `pom.xml`, `build.gradle` | `.java` |
| C# | `csharp-ls` | `*.csproj`, `*.sln` | `.cs` |
| PowerShell | `PowerShellEditorServices` | (config required) | `.ps1`, `.psm1`, `.psd1` |
| Ruby | `solargraph` | `Gemfile` | `.rb` |
| PHP | `intelephense` | `composer.json` | `.php` |
| Vue | `vue-language-server` | Package with Vue | `.vue` |
| Svelte | `svelte-language-server` | Package with Svelte | `.svelte` |
| Lua | `lua-language-server` | `.luarc.json` | `.lua` |
| Zig | `zls` | `build.zig` | `.zig` |
| Nix | `nil` | `flake.nix` | `.nix` |
| Deno | `deno lsp` | `deno.json` | `.ts`, `.tsx` (Deno projects) |
| ESLint | `vscode-eslint` | Package with ESLint | JS/TS files |

**Note:** PowerShell requires manual configuration in `opencode.json`:

```json
{
  "lsp": {
    "powershell": {
      "command": ["pwsh", "-NoLogo", "-NoProfile", "-Command",
        "Import-Module PowerShellEditorServices; Start-EditorServices -Stdio -HostName opencode -HostProfileId 0"],
      "extensions": [".ps1", ".psm1", ".psd1"]
    }
  }
}
```

### Server Spawning

Each server is configured with:

```typescript
interface Info {
  id: string                    // Unique identifier
  extensions: string[]          // File extensions handled
  root: RootFunction           // How to find project root
  spawn(root: string): Handle  // How to start the server
}

// Example: TypeScript server
export const Typescript: Info = {
  id: "typescript",
  extensions: [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
  root: NearestRoot(
    ["package-lock.json", "bun.lock", "pnpm-lock.yaml", "yarn.lock"],
    ["deno.json"]  // Exclude if Deno project
  ),
  async spawn(root) {
    const tsserver = await Bun.resolve("typescript/lib/tsserver.js", root)
    return {
      process: spawn("typescript-language-server", ["--stdio"], { cwd: root }),
      initialization: { tsserver: { path: tsserver } },
    }
  },
}
```

---

## LSP Operations

### Available Operations

```typescript
namespace LSP {
  // File operations
  function touchFile(path: string, wait?: boolean): Promise<void>

  // Diagnostics
  function diagnostics(): Promise<Record<string, Diagnostic[]>>

  // Navigation
  function definition(input: Position): Promise<Location[]>
  function references(input: Position): Promise<Location[]>
  function implementation(input: Position): Promise<Location[]>

  // Symbol search
  function workspaceSymbol(query: string): Promise<Symbol[]>
  function documentSymbol(uri: string): Promise<DocumentSymbol[]>

  // Information
  function hover(input: Position): Promise<Hover | null>

  // Call hierarchy
  function incomingCalls(input: Position): Promise<CallHierarchyItem[]>
  function outgoingCalls(input: Position): Promise<CallHierarchyItem[]>
}
```

### Usage Examples

```typescript
// Get all errors in project
const errors = await LSP.diagnostics()
for (const [file, diags] of Object.entries(errors)) {
  for (const d of diags) {
    console.log(`${file}:${d.range.start.line}: ${d.message}`)
  }
}

// Find where a function is defined
const defs = await LSP.definition({
  file: "src/app.ts",
  line: 42,
  character: 15,
})

// Find all usages of a symbol
const refs = await LSP.references({
  file: "src/utils.ts",
  line: 10,
  character: 20,
})

// Search for symbols by name
const symbols = await LSP.workspaceSymbol("UserService")
```

---

## Integration with AI Tools

### Edit Tool Integration

When the AI edits a file, LSP provides validation:

```typescript
// In edit.ts tool
async function execute(args, ctx) {
  // Make the edit
  await applyEdit(args.file_path, args.old_string, args.new_string)

  // Touch file to trigger LSP analysis
  await LSP.touchFile(args.file_path, true)  // Wait for diagnostics

  // Get any new errors
  const allDiagnostics = await LSP.diagnostics()
  const fileDiagnostics = allDiagnostics[args.file_path] || []

  // Include errors in response so AI can fix them
  const errors = fileDiagnostics
    .filter(d => d.severity === 1)  // Errors only
    .map(d => LSP.Diagnostic.pretty(d))

  return {
    output: errors.length > 0
      ? `Edit applied. Errors detected:\n${errors.join('\n')}`
      : 'Edit applied successfully.',
    metadata: { hasErrors: errors.length > 0 },
  }
}
```

### Read Tool Enhancement

LSP can enrich file reading with context:

```typescript
// Get document symbols to understand structure
const symbols = await LSP.documentSymbol(fileUri)

// AI receives file content + structure overview
return {
  content: fileContent,
  structure: symbols.map(s => `${s.kind}: ${s.name}`).join('\n'),
}
```

### Search Enhancement

LSP workspace symbol search is faster and more accurate than grep:

```typescript
// Instead of grepping for "class.*Service"
const services = await LSP.workspaceSymbol("Service")
  .filter(s => s.kind === SymbolKind.Class)

// Returns exact class definitions with file locations
```

---

## Configuration

### Enabling/Disabling LSP

```json
// opencode.json
{
  // Disable all LSP
  "lsp": false,

  // Or configure per-server
  "lsp": {
    "pyright": { "disabled": true },
    "rust-analyzer": { "disabled": false }
  }
}
```

### Custom Language Servers

```json
{
  "lsp": {
    "custom-kotlin": {
      "command": ["kotlin-language-server"],
      "extensions": [".kt", ".kts"],
      "env": {
        "JAVA_HOME": "/path/to/java"
      }
    }
  }
}
```

### Environment Variables

| Variable | Description |
|----------|-------------|
| `OPENCODE_DISABLE_LSP_DOWNLOAD` | Prevent auto-downloading language servers |
| `OPENCODE_EXPERIMENTAL_LSP_TOOL` | Enable experimental LSP tool for AI |
| `OPENCODE_EXPERIMENTAL_LSP_TY` | Use experimental Ty Python analyzer |

---

## Performance Considerations

### Server Lifecycle

- Servers are **lazily spawned** on first file access
- Multiple files in same project share one server
- Servers are **kept alive** for the session duration
- Graceful shutdown on session end

### Caching

- Diagnostics are cached per-file
- Symbol caches maintained by language servers
- File versions tracked for incremental updates

### Debouncing

```typescript
// Diagnostics debounced to allow semantic analysis to complete
const DIAGNOSTICS_DEBOUNCE_MS = 150

// Wait for final diagnostics (syntax → semantic)
await withTimeout(
  new Promise(resolve => {
    let timer
    Bus.subscribe(Event.Diagnostics, () => {
      clearTimeout(timer)
      timer = setTimeout(resolve, DIAGNOSTICS_DEBOUNCE_MS)
    })
  }),
  3000
)
```

---

## Why This Matters for AI Coding Agents

LSP integration enables OpenCode to:

1. **Understand code semantically** - Not just syntax, but types and relationships
2. **Validate changes immediately** - Catch errors before committing
3. **Navigate precisely** - Jump to exact definitions, not grep matches
4. **Support any language** - Single abstraction for all supported languages
5. **Stay current** - Language servers maintained by language teams
6. **Reduce hallucination** - Ground AI responses in actual type information

The result is an AI coding agent that truly understands your codebase at the same level as your IDE, not just as text files.
