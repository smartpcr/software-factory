# Migration Plan: LSP-Based Code Intelligence

> Last updated: 2026-01-23

This document outlines the plan to enhance the existing hypergraph-based code analysis system with LSP (Language Server Protocol) integration for compiler-level semantic understanding.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Current State Analysis](#current-state-analysis)
3. [Why LSP?](#why-lsp)
4. [Architecture Overview](#architecture-overview)
5. [Implementation Phases](#implementation-phases)
6. [Technical Design](#technical-design)
7. [NuGet Packages](#nuget-packages)
8. [Risk Assessment](#risk-assessment)
9. [Success Criteria](#success-criteria)

---

## Executive Summary

### Goal

Enhance the existing AST hypergraph system with LSP-based semantic enrichment to provide:
- **Type resolution** — Know return types, parameter types without reading all files
- **Cross-file navigation** — Find definitions and references with compiler accuracy
- **Real-time diagnostics** — Detect errors before running compiler
- **Richer embeddings** — Include type signatures for better semantic search

### Current vs Target State

| Capability | Current State | Target State |
|------------|---------------|--------------|
| Type information | ❌ None (AST syntax only) | ✅ Full type signatures via LSP |
| Cross-file refs | ❌ Pattern matching only | ✅ Compiler-accurate via LSP |
| Diagnostics | ❌ Must run compiler | ✅ Real-time via LSP |
| Embedding quality | ⚠️ Code + metadata | ✅ Code + types + docs + relationships |
| C# support | ✅ Roslyn AST | ✅ Roslyn AST + OmniSharp/csharp-ls |
| PowerShell support | ✅ PS AST | ✅ PS AST + PowerShellEditorServices |

### Estimated Effort

| Phase | Duration | Description |
|-------|----------|-------------|
| Phase 6A | 1 week | Create Ast.Lsp project with LSP client |
| Phase 6B | 1 week | Integrate LSP enrichment into embedding pipeline |
| Phase 6C | 1 week | CLI application with LSP commands |
| Phase 7 | 1 week | Integration testing and benchmarking |
| **Total** | **4 weeks** | |

---

## Current State Analysis

### Completed (Phases 1-5.5)

```
src/ast/
├── Ast.Core/                    ✅ Core models (HypergraphNode, Hyperedge, 32 edge types)
├── Ast.Csharp/                  ✅ C# parser via Roslyn (1048 lines, 62 tests)
├── Ast.PowerShell/              ✅ PowerShell parser (815 lines, 53 tests)
├── Ast.Embedding/               ✅ LLamaSharp + HTTP embedding (31 tests)
├── Ast.VectorDb/                ✅ Qdrant integration (28 tests)
├── Ast.GraphDb/                 ✅ Neo4j integration (35 tests)
├── Ast.Query/                   ✅ Fluent API + hybrid search (52 tests)
├── Ast.GraphDb.IntegrationTests/ ✅ Neo4j integration tests
└── docker-compose.yml           ✅ Neo4j + Qdrant infrastructure
```

**Test Coverage:** 347+ unit tests passing

### Gap Analysis

| Gap | Impact | Solution |
|-----|--------|----------|
| No type information | Embeddings miss semantic context | LSP hover/signature queries |
| No cross-file refs | Can't find callers/callees accurately | LSP references query |
| No diagnostics | Errors found late | LSP publishDiagnostics |
| No CLI tool | Hard to use system | Phase 6 implementation |
| Embedding quality | ~60% semantic capture | LSP enrichment → ~90% |

---

## Why LSP?

### Comparison: AST-Only vs LSP-Enriched

From `code_understanding_comparison.md`:

| Feature | AST-Only (Current) | LSP-Enriched (Target) |
|---------|-------------------|----------------------|
| Type information | ❌ | ✅ |
| Cross-file navigation | ❌ | ✅ |
| Import resolution | ❌ | ✅ |
| Path alias support | ❌ | ✅ |
| Error diagnostics | Limited | ✅ Full |
| Refactoring support | ❌ | ✅ |
| Build config awareness | ❌ | ✅ |

### Real-World Example

```csharp
// Current AST parsing knows:
// - There's a method "ProcessOrder"
// - It takes some parameters
// - It calls something

// LSP enrichment adds:
// - Full signature: Task<OrderResult> ProcessOrder(Order order, CancellationToken ct)
// - Return type: Task<OrderResult>
// - Parameters: [{name: "order", type: "Order"}, {name: "ct", type: "CancellationToken"}]
// - Called by: ["OrderController.Post", "BatchProcessor.Run", ...]
// - Diagnostics: Warning CS1998 - async method lacks 'await'
```

### Embedding Quality Impact

From `embedding_model_approach.md`:

```
Pure Embeddings:     ████░░░░░░  (captures ~40%)
AST + Embeddings:    ██████░░░░  (captures ~60%)  ← Current
SCIP + Embeddings:   ████████░░  (captures ~80%)
LSP + Embeddings:    ██████████  (captures ~95%)  ← Target
```

---

## Architecture Overview

### Current Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Current Hypergraph Architecture               │
│                                                                  │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────────┐   │
│  │ Ast.Csharp   │    │ Ast.PowerShell│   │   Ast.Core       │   │
│  │ (Roslyn AST) │    │ (PS AST)      │   │   (Hypergraph)   │   │
│  └──────┬───────┘    └──────┬───────┘    └──────────────────┘   │
│         │                   │                                    │
│         └─────────┬─────────┘                                    │
│                   ▼                                              │
│         ┌─────────────────────┐                                  │
│         │ RepositoryProcessor │                                  │
│         └─────────┬───────────┘                                  │
│                   ▼                                              │
│         ┌─────────────────────┐                                  │
│         │   HypergraphData    │                                  │
│         │   (Nodes + Edges)   │                                  │
│         └─────────┬───────────┘                                  │
│                   │                                              │
│         ┌─────────┴─────────┐                                    │
│         ▼                   ▼                                    │
│  ┌─────────────────┐ ┌─────────────────┐                        │
│  │  Ast.Embedding  │ │  Ast.GraphDb    │                        │
│  │  (nomic-embed)  │ │  (Neo4j)        │                        │
│  └────────┬────────┘ └─────────────────┘                        │
│           ▼                                                      │
│  ┌─────────────────┐                                            │
│  │  Ast.VectorDb   │                                            │
│  │  (Qdrant)       │                                            │
│  └─────────────────┘                                            │
└─────────────────────────────────────────────────────────────────┘
```

### Target Architecture (with LSP)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    Target Hypergraph + LSP Architecture                  │
│                                                                          │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────────────────┐   │
│  │ Ast.Csharp   │    │ Ast.PowerShell│   │   Ast.Core               │   │
│  │ (Roslyn AST) │    │ (PS AST)      │   │   (Hypergraph + LSP      │   │
│  └──────┬───────┘    └──────┬───────┘    │    enrichment models)    │   │
│         │                   │            └──────────────────────────┘   │
│         └─────────┬─────────┘                                           │
│                   ▼                                                      │
│         ┌─────────────────────┐                                         │
│         │ RepositoryProcessor │                                         │
│         └─────────┬───────────┘                                         │
│                   ▼                                                      │
│         ┌─────────────────────┐                                         │
│         │   HypergraphData    │                                         │
│         │   (Nodes + Edges)   │                                         │
│         └─────────┬───────────┘                                         │
│                   │                                                      │
│                   ▼                                                      │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │                      Ast.Lsp (NEW)                               │    │
│  │                                                                  │    │
│  │  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐                │    │
│  │  │ LspClient   │ │OmniSharp   │ │  PSES       │                │    │
│  │  │ (JSON-RPC)  │ │(C#)        │ │(PowerShell) │                │    │
│  │  └─────────────┘ └─────────────┘ └─────────────┘                │    │
│  │                                                                  │    │
│  │  Provides: Type signatures, References, Diagnostics, Hover      │    │
│  └──────────────────────────┬──────────────────────────────────────┘    │
│                             │                                            │
│                             ▼                                            │
│         ┌─────────────────────────────────────────┐                     │
│         │        Enriched HypergraphData          │                     │
│         │  (Nodes with Signature, References,     │                     │
│         │   Documentation, Diagnostics)           │                     │
│         └─────────────────────┬───────────────────┘                     │
│                               │                                          │
│              ┌────────────────┼────────────────┐                        │
│              ▼                ▼                ▼                        │
│     ┌─────────────────┐ ┌─────────────┐ ┌─────────────────┐            │
│     │  Ast.Embedding  │ │ Ast.GraphDb │ │  Ast.VectorDb   │            │
│     │  (Voyage/OpenAI │ │  (Neo4j)    │ │  (Qdrant)       │            │
│     │   or local)     │ │             │ │                 │            │
│     └─────────────────┘ └─────────────┘ └─────────────────┘            │
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │                      Ast.Query (Enhanced)                        │    │
│  │         (Hybrid: Graph + Semantic + Type-aware search)          │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │                        Ast.Cli (NEW)                             │    │
│  │              (index, search, callers, impact, serve)            │    │
│  └─────────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Implementation Phases

### Phase 6A: Create Ast.Lsp Project (Week 1)

**Objective:** Implement LSP client for C# and PowerShell language servers.

**Tasks:**

1. **Create Ast.Lsp project**
   ```
   src/ast/Ast.Lsp/
   ├── ILspClient.cs           # Client interface
   ├── LspClient.cs            # JSON-RPC client implementation
   ├── LspEnrichmentService.cs # High-level enrichment service
   ├── LspServerConfig.cs      # Server configuration
   ├── LspServerManager.cs     # Server lifecycle management
   └── ServiceCollectionExtensions.cs
   ```

2. **Implement LSP client**
   - JSON-RPC over stdio communication
   - Support for standard LSP messages:
     - `textDocument/hover` — Get type signatures
     - `textDocument/definition` — Find definitions
     - `textDocument/references` — Find all usages
     - `textDocument/documentSymbol` — Get file structure
     - `textDocument/publishDiagnostics` — Receive errors/warnings
     - `callHierarchy/incomingCalls` — Get callers
     - `callHierarchy/outgoingCalls` — Get callees

3. **Configure language servers**
   - **C#:** OmniSharp or csharp-ls
   - **PowerShell:** PowerShellEditorServices (PSES)

4. **Create unit tests (Ast.Lsp.UnitTests)**

**Deliverables:**
- [ ] Ast.Lsp.csproj created
- [ ] ILspClient interface
- [ ] LspClient implementation
- [ ] LspEnrichmentService
- [ ] 30+ unit tests

---

### Phase 6B: Integrate LSP Enrichment (Week 2)

**Objective:** Integrate LSP enrichment into the embedding pipeline.

**Tasks:**

1. **Extend HypergraphNode metadata**
   ```csharp
   // Add to HypergraphNode.Metadata dictionary:
   - "Signature"      // Full type signature
   - "ReturnType"     // Return type for methods
   - "Parameters"     // Parameter list with types
   - "Documentation"  // XML doc or hover content
   - "CalledBy"       // List of callers (from LSP)
   - "Diagnostics"    // Warnings/errors
   ```

2. **Enhance GetEnhancedEmbeddingText()**
   ```csharp
   public string GetEnhancedEmbeddingText()
   {
       var parts = new List<string>();

       // 1. Symbol identification
       parts.Add($"{NodeType}: {Name}");

       // 2. Documentation (from LSP hover)
       if (Metadata.TryGetValue("Documentation", out var doc))
           parts.Add(doc);

       // 3. Type signature (from LSP hover)
       if (Metadata.TryGetValue("Signature", out var sig))
           parts.Add($"Signature: {sig}");

       // 4. Parameters (from LSP)
       if (Metadata.TryGetValue("Parameters", out var param))
           parts.Add($"Parameters: {param}");

       // 5. Relationships (from LSP references)
       if (Metadata.TryGetValue("CalledBy", out var callers))
           parts.Add($"Called by: {callers}");

       // 6. Actual code
       parts.Add(SourceCode);

       return string.Join("\n", parts);
   }
   ```

3. **Create EnhancedIndexingPipeline**
   ```
   Extract AST → Enrich with LSP → Generate embeddings → Store
   ```

4. **Update CodeEmbeddingService to use LSP enrichment**

5. **Add configuration options**
   ```csharp
   public class AstConfiguration
   {
       // Existing...

       public LspConfiguration Lsp { get; set; } = new();
   }

   public class LspConfiguration
   {
       public bool Enabled { get; set; } = true;
       public bool UseCsharpLs { get; set; } = true;
       public bool UsePses { get; set; } = true;
       public string CsharpLsPath { get; set; } = "csharp-ls";
       public string PsesPath { get; set; } = "pwsh";
       public int TimeoutMs { get; set; } = 30000;
   }
   ```

**Deliverables:**
- [ ] HypergraphNode enhanced with LSP metadata
- [ ] GetEnhancedEmbeddingText() implemented
- [ ] EnhancedIndexingPipeline created
- [ ] Configuration options added
- [ ] Unit tests updated

---

### Phase 6C: CLI Application (Week 3)

**Objective:** Create command-line interface for all operations.

**Tasks:**

1. **Create Ast.Cli project**
   ```
   src/ast/Ast.Cli/
   ├── Program.cs
   ├── Commands/
   │   ├── IndexCommand.cs      # Index repository
   │   ├── SearchCommand.cs     # Semantic search
   │   ├── CallersCommand.cs    # Find callers
   │   ├── CalleesCommand.cs    # Find callees
   │   ├── ImpactCommand.cs     # Change impact analysis
   │   ├── SimilarCommand.cs    # Find similar code
   │   ├── ExportCommand.cs     # Export to JSON/HyperNetX
   │   ├── DiagnosticsCommand.cs# Show LSP diagnostics
   │   └── ServeCommand.cs      # Start HTTP API server
   └── appsettings.json
   ```

2. **Implement commands using Spectre.Console.Cli**

3. **Add progress reporting and rich output**

**Example Usage:**

```bash
# Index a repository with LSP enrichment
ast-cli index /path/to/repo --use-lsp --model voyage-code-3

# Search for code semantically
ast-cli search "authenticate user with OAuth" --top 10

# Find all callers of a method
ast-cli callers "UserService.ValidateCredentials"

# Analyze change impact
ast-cli impact "src/Auth/TokenValidator.cs:45"

# Show LSP diagnostics
ast-cli diagnostics /path/to/repo --severity error

# Export hypergraph
ast-cli export ./output --format hypernetx

# Start API server
ast-cli serve --port 8080
```

**Deliverables:**
- [ ] Ast.Cli.csproj created
- [ ] All commands implemented
- [ ] Progress reporting with Spectre.Console
- [ ] Error handling and logging
- [ ] Configuration via appsettings.json

---

### Phase 7: Integration Testing (Week 4)

**Objective:** End-to-end testing with real repositories.

**Tasks:**

1. **Enhance Ast.IntegrationTests**
   - Test full pipeline: Parse → LSP Enrich → Embed → Store → Query
   - Test with real C# and PowerShell repositories
   - Verify embedding quality improvement

2. **Create LSP-specific integration tests**
   ```gherkin
   Feature: LSP Enrichment

   @integration
   Scenario: Enrich C# method with type information
     Given a C# file with method "ProcessOrder(Order order)"
     When I enrich the hypergraph with LSP
     Then the method node should have metadata:
       | Key        | Contains                    |
       | Signature  | Task<OrderResult> ProcessOrder |
       | ReturnType | Task<OrderResult>           |
       | Parameters | order: Order                |

   @integration
   Scenario: Find all callers via LSP
     Given an indexed repository with method "ValidateToken"
     When I query for callers of "ValidateToken"
     Then I should receive accurate caller list from LSP
     And the results should match actual references
   ```

3. **Benchmark embedding quality**
   - Compare search relevance: AST-only vs LSP-enriched
   - Measure precision/recall on known queries

4. **Performance testing**
   - LSP server startup time
   - Enrichment throughput (nodes/second)
   - Memory usage with large repositories

**Deliverables:**
- [ ] Integration tests for LSP enrichment
- [ ] Benchmark results documented
- [ ] Performance metrics established

---

## Technical Design

### Ast.Lsp Project Structure

```csharp
// ILspClient.cs
namespace Ast.Lsp;

public interface ILspClient : IAsyncDisposable
{
    Task InitializeAsync(string rootPath, CancellationToken ct = default);

    Task<HoverResult?> GetHoverAsync(
        string filePath, int line, int character, CancellationToken ct = default);

    Task<IReadOnlyList<Location>> GetDefinitionsAsync(
        string filePath, int line, int character, CancellationToken ct = default);

    Task<IReadOnlyList<Location>> GetReferencesAsync(
        string filePath, int line, int character, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        string filePath, CancellationToken ct = default);

    Task<IReadOnlyList<CallHierarchyItem>> GetIncomingCallsAsync(
        string filePath, int line, int character, CancellationToken ct = default);

    Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        string filePath, CancellationToken ct = default);
}

// LspEnrichmentService.cs
namespace Ast.Lsp;

public class LspEnrichmentService : IAsyncDisposable
{
    private readonly Dictionary<string, ILspClient> _clients = new();
    private readonly LspConfiguration _config;

    public async Task<HypergraphNode> EnrichNodeAsync(
        HypergraphNode node, CancellationToken ct = default)
    {
        var client = await GetClientForLanguageAsync(node.Language, node.FilePath, ct);
        if (client is null) return node;

        var metadata = new Dictionary<string, string>(node.Metadata);

        // Get hover info for type signature
        var hover = await client.GetHoverAsync(
            node.FilePath, node.LineStart, 0, ct);
        if (hover is not null)
        {
            metadata["Signature"] = ExtractSignature(hover);
            metadata["Documentation"] = ExtractDocumentation(hover);
        }

        // Get references for call graph
        var refs = await client.GetReferencesAsync(
            node.FilePath, node.LineStart, 0, ct);
        if (refs.Count > 0)
        {
            metadata["CalledBy"] = string.Join(",",
                refs.Select(r => $"{Path.GetFileName(r.Uri)}:{r.Range.Start.Line}").Take(20));
        }

        return node with { Metadata = metadata };
    }

    public async Task<IReadOnlyList<HypergraphNode>> EnrichBatchAsync(
        IReadOnlyList<HypergraphNode> nodes,
        IProgress<EnrichmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Group by file to minimize LSP calls
        var byFile = nodes.GroupBy(n => n.FilePath);
        var results = new List<HypergraphNode>();
        var processed = 0;

        foreach (var group in byFile)
        {
            foreach (var node in group)
            {
                ct.ThrowIfCancellationRequested();
                var enriched = await EnrichNodeAsync(node, ct);
                results.Add(enriched);
                processed++;
                progress?.Report(new EnrichmentProgress(processed, nodes.Count, node.FilePath));
            }
        }

        return results;
    }
}
```

### Enhanced Embedding Pipeline

```csharp
// EnhancedIndexingPipeline.cs
namespace Ast.Pipeline;

public class EnhancedIndexingPipeline
{
    private readonly RepositoryProcessor _processor;
    private readonly LspEnrichmentService _lspEnricher;
    private readonly IEmbeddingGenerator _embedder;
    private readonly IGraphStore _graphStore;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<EnhancedIndexingPipeline> _logger;

    public async Task IndexRepositoryAsync(
        string repoPath,
        IndexingOptions options,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Phase 1: Extract hypergraph from AST
        progress?.Report(new("Extracting code structure...", 0));
        var hypergraph = await _processor.ProcessRepositoryAsync(repoPath, ct);
        _logger.LogInformation("Extracted {Nodes} nodes, {Edges} edges",
            hypergraph.Nodes.Count, hypergraph.Edges.Count);

        // Phase 2: Enrich with LSP (if enabled)
        if (options.UseLspEnrichment)
        {
            progress?.Report(new("Enriching with LSP data...", 25));
            var enrichedNodes = await _lspEnricher.EnrichBatchAsync(
                hypergraph.Nodes.ToList(), ct: ct);
            hypergraph = hypergraph with { Nodes = enrichedNodes };
            _logger.LogInformation("LSP enrichment complete");
        }

        // Phase 3: Generate embeddings with enhanced text
        progress?.Report(new("Generating embeddings...", 50));
        var embeddingInputs = hypergraph.Nodes
            .Select(n => (Node: n, Text: n.GetEnhancedEmbeddingText()))
            .ToList();

        var embeddings = await _embedder.GenerateBatchEmbeddingsAsync(
            embeddingInputs.Select(x => x.Text).ToList(), ct);

        // Phase 4: Store in graph database
        progress?.Report(new("Storing in graph database...", 70));
        await _graphStore.UpsertGraphAsync(hypergraph, ct);

        // Phase 5: Store in vector database
        progress?.Report(new("Storing in vector database...", 85));
        var nodeEmbeddings = embeddingInputs.Zip(embeddings, (input, emb) =>
            new NodeEmbedding
            {
                NodeId = input.Node.Id,
                Vector = emb,
                EmbeddingModel = _embedder.Name
            }).ToList();

        await _vectorStore.UpsertBatchAsync(nodeEmbeddings, ct);

        progress?.Report(new("Indexing complete!", 100));
    }
}
```

---

## NuGet Packages

### New Packages to Add

```xml
<!-- Directory.Packages.props additions -->

<!-- LSP Client (Phase 6A) -->
<PackageVersion Include="OmniSharp.Extensions.LanguageClient" Version="0.19.9" />
<PackageVersion Include="StreamJsonRpc" Version="2.17.8" />

<!-- CLI (Phase 6C) -->
<PackageVersion Include="Spectre.Console" Version="0.53.0" />
<PackageVersion Include="Spectre.Console.Cli" Version="0.53.0" />

<!-- Enhanced Embedding (Optional - for Voyage API) -->
<PackageVersion Include="Microsoft.SemanticKernel" Version="1.6.3" />
```

### Ast.Lsp.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="OmniSharp.Extensions.LanguageClient" />
    <PackageReference Include="StreamJsonRpc" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ast.Core\Ast.Core.csproj" />
  </ItemGroup>
</Project>
```

### Ast.Cli.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Spectre.Console" />
    <PackageReference Include="Spectre.Console.Cli" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog.Extensions.Hosting" />
    <PackageReference Include="Serilog.Sinks.Console" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ast.Core\Ast.Core.csproj" />
    <ProjectReference Include="..\Ast.Csharp\Ast.Csharp.csproj" />
    <ProjectReference Include="..\Ast.PowerShell\Ast.PowerShell.csproj" />
    <ProjectReference Include="..\Ast.Lsp\Ast.Lsp.csproj" />
    <ProjectReference Include="..\Ast.Embedding\Ast.Embedding.csproj" />
    <ProjectReference Include="..\Ast.VectorDb\Ast.VectorDb.csproj" />
    <ProjectReference Include="..\Ast.GraphDb\Ast.GraphDb.csproj" />
    <ProjectReference Include="..\Ast.Query\Ast.Query.csproj" />
  </ItemGroup>
</Project>
```

---

## Risk Assessment

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| LSP server crashes | High | Medium | Graceful error handling, fallback to AST-only |
| LSP startup latency | Medium | High | Lazy initialization, server pooling |
| Memory usage with multiple servers | Medium | Medium | One server per language, dispose when idle |
| OmniSharp vs csharp-ls compatibility | Low | Low | Support both, make configurable |
| PSES setup complexity | Medium | Medium | Document setup, provide Docker option |
| Large repo performance | High | Medium | Batch processing, parallel enrichment |

### Fallback Strategy

If LSP enrichment fails for a node, fall back to AST-only metadata:

```csharp
try
{
    enrichedNode = await _lspEnricher.EnrichNodeAsync(node, ct);
}
catch (LspException ex)
{
    _logger.LogWarning("LSP enrichment failed for {Node}: {Error}", node.Id, ex.Message);
    enrichedNode = node; // Use original AST-only node
}
```

---

## Success Criteria

### Functional

- [ ] LSP client connects to csharp-ls and PSES
- [ ] Type signatures extracted via hover
- [ ] References found via LSP
- [ ] Diagnostics captured from LSP
- [ ] CLI commands all functional
- [ ] Integration tests pass

### Performance

- [ ] LSP enrichment < 100ms per node
- [ ] Full repo indexing (10K files) < 10 minutes
- [ ] Memory usage < 2GB for typical repo

### Quality

- [ ] Search relevance improved by > 20% (benchmark)
- [ ] Type-aware queries return accurate results
- [ ] Zero false positives in reference finding

---

## Summary

This migration plan enhances the existing hypergraph system with LSP-based semantic understanding, achieving:

1. **Compiler-level accuracy** for type resolution and references
2. **Richer embeddings** with type signatures and documentation
3. **Real-time diagnostics** without running the compiler
4. **CLI tool** for easy interaction with the system

The phased approach (4 weeks) allows for incremental delivery while maintaining system stability. The fallback strategy ensures the system remains functional even if LSP servers are unavailable.

---

## References

- [LSP Deep Dive](./lsp-deep-dive.md) — How LSP provides semantic understanding
- [Embedding Model Approach](./embedding_model_approach.md) — Chunking and enrichment strategies
- [Code Understanding Comparison](./code_understanding_comparison.md) — LSP vs other approaches
- [AST Implementation Plan](./AST_IMPLEMENTATION_PLAN.md) — Original implementation plan
- [AST Status](./AST_STATUS.md) — Current implementation status
