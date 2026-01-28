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

---

## C# LSP Setup and Usage in .NET

This section covers how to use LSP in C# applications using the `Ast.Lsp` library.

### Installing Language Servers

#### csharp-ls (Recommended - Lightweight)

The `csharp-ls` language server is a lightweight, fast option for C# code intelligence:

```bash
# Install globally
dotnet tool install -g csharp-ls

# Verify installation
csharp-ls --version
```

**Advantages:**
- Fast startup time
- Low memory footprint
- Works well with solution files
- No additional dependencies

#### OmniSharp (Full-Featured)

OmniSharp provides comprehensive C# language services:

```bash
# Download from GitHub releases
# https://github.com/OmniSharp/omnisharp-roslyn/releases

# Or install via package manager (macOS)
brew install omnisharp
```

**Advantages:**
- Full Roslyn-powered analysis
- Better support for complex solutions
- More LSP features (refactoring, code actions)

### Configuration

Configure the LSP client through `LspConfiguration`:

```csharp
using Ast.Lsp;
using Microsoft.Extensions.Options;

// Configure LSP settings
var config = new LspConfiguration
{
    Enabled = true,
    TimeoutMs = 30000,  // 30 second timeout
    MaxReferences = 100,
    CollectDiagnostics = true,
    CollectCallHierarchy = true,

    CSharp = new CSharpLspConfiguration
    {
        Enabled = true,
        ServerType = CSharpLspServerType.CSharpLs,  // or OmniSharp
        ServerPath = null  // Uses PATH if null
    },

    PowerShell = new PowerShellLspConfiguration
    {
        Enabled = true,
        LogLevel = PowerShellLspLogLevel.Normal
    }
};
```

### Dependency Injection Setup

Register LSP services in your DI container:

```csharp
using Ast.Lsp;

// In Program.cs or Startup.cs
services.AddLspServices(options =>
{
    options.Enabled = true;
    options.CSharp.Enabled = true;
    options.CSharp.ServerType = CSharpLspServerType.CSharpLs;
});

// Or use convenience methods
services.AddCSharpLsp();       // C# only
services.AddPowerShellLsp();   // PowerShell only
services.AddAllLspServices();  // Both languages
```

### Using the LSP Client

#### Basic Usage

```csharp
using Ast.Lsp;

// Get or create a client for a workspace
var serverManager = serviceProvider.GetRequiredService<LspServerManager>();
var client = await serverManager.GetOrCreateClientAsync(
    LspLanguage.CSharp,
    workspacePath: "/path/to/solution");

// Open a document
var content = await File.ReadAllTextAsync(filePath);
await client.OpenDocumentAsync(filePath, content);

// Get hover information (type signatures)
var hover = await client.GetHoverAsync(filePath, line: 10, character: 15);
Console.WriteLine($"Type info: {hover?.Contents}");

// Get definitions (go to definition)
var definitions = await client.GetDefinitionsAsync(filePath, line: 10, character: 15);
foreach (var def in definitions)
{
    Console.WriteLine($"Defined at: {def.Uri}:{def.Range.Start.Line}");
}

// Get references (find all usages)
var references = await client.GetReferencesAsync(filePath, line: 10, character: 15);
Console.WriteLine($"Found {references.Count} references");

// Get document symbols (outline)
var symbols = await client.GetDocumentSymbolsAsync(filePath);
foreach (var symbol in symbols)
{
    Console.WriteLine($"{symbol.Kind}: {symbol.Name}");
}

// Close document when done
await client.CloseDocumentAsync(filePath);
```

#### Workspace Symbol Search

```csharp
// Search for symbols across the workspace
var symbols = await client.GetWorkspaceSymbolsAsync("UserService");
foreach (var symbol in symbols)
{
    Console.WriteLine($"{symbol.Kind} {symbol.Name} in {symbol.Location.Uri}");
}
```

#### Call Hierarchy

```csharp
// Get incoming calls (who calls this method)
var incomingCalls = await client.GetIncomingCallsAsync(filePath, line, character);
foreach (var call in incomingCalls)
{
    Console.WriteLine($"Called by: {call.From.Name}");
}

// Get outgoing calls (what does this method call)
var outgoingCalls = await client.GetOutgoingCallsAsync(filePath, line, character);
foreach (var call in outgoingCalls)
{
    Console.WriteLine($"Calls: {call.To.Name}");
}
```

#### Diagnostics

```csharp
// Subscribe to diagnostics events
client.DiagnosticsReceived += (sender, args) =>
{
    foreach (var diagnostic in args.Diagnostics)
    {
        Console.WriteLine($"{diagnostic.Severity}: {diagnostic.Message}");
    }
};

// Or get diagnostics for a specific file
var diagnostics = client.GetDiagnostics(filePath);
```

### Enriching Hypergraph Nodes with LSP Data

The `LspEnrichmentService` enriches AST nodes with semantic data from LSP:

```csharp
using Ast.Lsp;
using Ast.Core;

var enrichmentService = serviceProvider.GetRequiredService<LspEnrichmentService>();

// Enrich a single node
var enrichedNode = await enrichmentService.EnrichNodeAsync(node);

// Enrich multiple nodes (optimized for batch processing)
var enrichedNodes = await enrichmentService.EnrichBatchAsync(nodes);

// Enrich an entire hypergraph
var enrichedGraph = await enrichmentService.EnrichHypergraphAsync(hypergraph);

// Access enriched metadata
var signature = enrichedNode.Metadata.GetValueOrDefault("Signature");
var returnType = enrichedNode.Metadata.GetValueOrDefault("ReturnType");
var callers = enrichedNode.Metadata.GetValueOrDefault("Callers") as List<string>;
var referenceCount = enrichedNode.Metadata.GetValueOrDefault("ReferenceCount");
```

### Enhanced Embedding Text

Use LSP-enriched metadata for better embeddings:

```csharp
// The GetEnhancedEmbeddingText() method includes LSP data
var embeddingText = node.GetEnhancedEmbeddingText();

// This includes:
// - Symbol name and type
// - Full type signature from LSP hover
// - Documentation/docstrings
// - Callers and callees (call hierarchy)
// - Reference count
// - Original code content
```

### Integration Test Example

See `Ast.Lsp.IntegrationTests` for real-world examples:

```csharp
[TestClass]
[TestCategory("lsp")]
public class CSharpRepoLspTests
{
    private static LspServerManager _serverManager;
    private static ILspClient _lspClient;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        var config = new LspConfiguration
        {
            Enabled = true,
            CSharp = new CSharpLspConfiguration
            {
                Enabled = true,
                ServerType = CSharpLspServerType.CSharpLs
            }
        };

        _serverManager = new LspServerManager(
            Options.Create(config),
            loggerFactory);

        _lspClient = await _serverManager.GetOrCreateClientAsync(
            LspLanguage.CSharp,
            workspacePath);
    }

    [TestMethod]
    public async Task ExtractRepoMetadata()
    {
        // Find entry points
        var symbols = await _lspClient.GetDocumentSymbolsAsync("Program.cs");
        var mainMethod = symbols.FirstOrDefault(s => s.Name == "Main");

        // Get type information
        var hover = await _lspClient.GetHoverAsync(
            "Program.cs",
            mainMethod.Range.Start.Line,
            mainMethod.Range.Start.Character);

        // Find all controller classes
        var controllers = await _lspClient.GetWorkspaceSymbolsAsync("Controller");
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _serverManager.DisposeAsync();
    }
}
```

### Repository Analysis Features

The `Ast.Lsp.IntegrationTests` project demonstrates LSP-powered repository analysis:

#### C# Repository Analysis

1. **Entry Point Detection**: Finds `Main` methods, top-level statements, `WebApplication.CreateBuilder`
2. **DI Registration Extraction**: Detects `AddSingleton`, `AddScoped`, `AddTransient`, Unity container registrations
3. **API Controller Mapping**: Extracts routes, HTTP methods, controller hierarchies
4. **Component Diagrams**: Generates Mermaid diagrams showing project/namespace dependencies

#### PowerShell Repository Analysis

1. **Module Discovery**: Finds `.psm1` and `.psd1` files with their exports
2. **Class Hierarchy**: Maps class inheritance across modules
3. **Interaction Diagrams**: Shows module imports and function calls
4. **Dependency Graphs**: Visualizes `using module` and `Import-Module` relationships

### Output Formats

Test outputs include:

```
output/lsp/
├── csharp/
│   ├── _lsp_repo_metadata.json    # Entry points, DI, API controllers
│   ├── _component_diagram.json     # Nodes and edges
│   └── _component_diagram.mmd      # Mermaid diagram
└── powershell/
    ├── _interaction_diagram.json   # Modules and interactions
    ├── _sequence_diagram.mmd       # Mermaid sequence diagram
    ├── _class_diagram.mmd          # Mermaid class diagram
    └── _module_hierarchy.json      # Module dependency graph
```

### PowerShell Editor Services Setup

PowerShell Editor Services (PSES) provides LSP support for PowerShell. Unlike csharp-ls, it's not distributed via NuGet but through the VS Code extension or GitHub releases.

#### Option 1: VS Code Extension (Recommended)

1. Install the PowerShell extension in VS Code
2. The extension automatically installs PSES to:
   - Windows: `%USERPROFILE%\.vscode\extensions\ms-vscode.powershell-*\modules`
   - macOS/Linux: `~/.vscode/extensions/ms-vscode.powershell-*/modules`

3. Configure the path in your application:
```csharp
var config = new LspConfiguration
{
    PowerShell = new PowerShellLspConfiguration
    {
        Enabled = true,
        EditorServicesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions", "ms-vscode.powershell-2024.0.0", // adjust version
            "modules", "PowerShellEditorServices")
    }
};
```

#### Option 2: GitHub Release

1. Download from [PowerShell Editor Services Releases](https://github.com/PowerShell/PowerShellEditorServices/releases)
2. Extract to a known location
3. Configure the path in your application

#### Option 3: Manual Build

```bash
git clone https://github.com/PowerShell/PowerShellEditorServices.git
cd PowerShellEditorServices
./build.ps1
```

### Troubleshooting

#### LSP Server Not Starting

```bash
# Check if csharp-ls is installed
which csharp-ls
dotnet tool list -g | grep csharp-ls

# Reinstall if needed
dotnet tool uninstall -g csharp-ls
dotnet tool install -g csharp-ls

# For PowerShell, ensure PowerShell 7+ is installed
pwsh --version

# Check if PSES module exists
ls ~/.vscode/extensions/ms-vscode.powershell-*/modules/PowerShellEditorServices
```

#### Timeout Issues

```csharp
// Increase timeout for large repositories
var config = new LspConfiguration
{
    TimeoutMs = 120000  // 2 minutes
};
```

#### Server Crashes

Check logs and ensure:
- The solution/project builds successfully
- No syntax errors in the codebase
- Sufficient memory available

---

## References

- [LSP Specification](https://microsoft.github.io/language-server-protocol/specifications/specification-current/)
- [csharp-ls GitHub](https://github.com/razzmatazz/csharp-language-server)
- [OmniSharp GitHub](https://github.com/OmniSharp/omnisharp-roslyn)
- [PowerShell Editor Services](https://github.com/PowerShell/PowerShellEditorServices)
