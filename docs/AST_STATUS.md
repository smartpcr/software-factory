# AST HyperGraph Reasoning - Project Status

Last Updated: 2026-01-23

## Overview

Building a HyperGraph-based code reasoning system inspired by [HyperGraphReasoning](https://github.com/smartpcr/hypergraphreasoning), adapted for C#/.NET with local LLM support and LSP-based code intelligence.

---

## Current Phase: Phase 6C Complete - CLI Application

### Completed Tasks

- [x] Analyzed existing codebase structure
- [x] Reviewed HyperGraphReasoning reference project
- [x] Identified gaps between current state and target state
- [x] Selected technology stack (Neo4j, Qdrant, HTTP-based embedding API)
- [x] Created detailed implementation plan
- [x] Documented architectural decisions
- [x] **Phase 1: Created test projects and configuration system**
  - [x] Created Ast.Core.UnitTests (51 tests passing)
  - [x] Created Ast.Csharp.UnitTests (41 tests passing)
  - [x] Created Ast.PowerShell.UnitTests (33 tests passing)
  - [x] Added AstConfiguration with nested EmbeddingConfiguration, GraphDbConfiguration, VectorDbConfiguration
  - [x] Updated solution file with new projects
- [x] **Documentation: Created README.md files for AST libraries**
  - [x] Created Ast.Csharp/README.md - Documenting C# metadata extraction
  - [x] Created Ast.PowerShell/README.md - Documenting PowerShell metadata extraction
- [x] **Enhanced PowerShell Extractor with module and remote execution tracking**
  - [x] Added Import-Module tracking with version, prefix, alias support
  - [x] Added module-qualified function calls (ModuleName\FunctionName)
  - [x] Added remote execution tracking (Invoke-Command, New-PSSession, etc.)
  - [x] Tracks credentials, computer names, sessions, script blocks
  - [x] Added 20 new unit tests (53 total PowerShell tests)
- [x] **Enhanced C# Extractor with architectural pattern extraction**
  - [x] Added DI registration tracking (AddSingleton, AddScoped, AddTransient, AddDbContext)
  - [x] Added API endpoint extraction (routes, HTTP methods, parameter bindings)
  - [x] Added entry point detection (Main method, top-level statements, minimal hosting)
  - [x] Added HttpClient usage tracking (external API calls)
  - [x] Added database access tracking (DbContext, SqlConnection)
  - [x] Added logging pattern extraction (ILogger with structured fields)
  - [x] Added configuration access tracking (IConfiguration)
  - [x] Added middleware pipeline tracking (UseAuthentication, etc.)
  - [x] Added file system access tracking
  - [x] Added 21 new unit tests (62 total C# tests)
- [x] **Integration Tests: Real repository parsing**
  - [x] Created Ast.IntegrationTests project
  - [x] C# repo parsing tests (5 tests) - E:\work\hci\update
  - [x] PowerShell repo parsing tests (6 tests) - E:\work\hub\sln\Deploy
  - [x] JSON hypergraph output to test output directory
- [x] **Phase 2: Embedding Generation**
  - [x] Created Ast.Embedding project
  - [x] Implemented IEmbeddingGenerator interface
  - [x] Implemented LLamaEmbeddingGenerator (local GGUF models via LLamaSharp)
  - [x] Implemented HttpEmbeddingGenerator (Ollama, OpenAI-compatible APIs)
  - [x] Implemented CodeEmbeddingService (hypergraph node/edge embeddings)
  - [x] Added EmbeddingGeneratorFactory and DI extensions
  - [x] Created Ast.Embedding.UnitTests (65 tests passing)
  - [x] Added LLamaSharp packages to Directory.Packages.props
- [x] **Phase 3: Vector Database**
  - [x] Created Ast.VectorDb project
  - [x] Implemented IVectorStore interface
  - [x] Implemented QdrantVectorStore (Qdrant gRPC client)
  - [x] Implemented CodeVectorService (indexing and semantic search)
  - [x] Added filter support (language, node type, file path)
  - [x] Added DI service collection extensions
  - [x] Created Ast.VectorDb.UnitTests (28 tests passing)
  - [x] Added Qdrant.Client package to Directory.Packages.props
- [x] **Phase 4: Graph Database**
  - [x] Created Ast.GraphDb project
  - [x] Implemented IGraphStore interface
  - [x] Implemented Neo4jGraphStore (Cypher queries, batch operations)
  - [x] Implemented GraphQueryService (high-level code analysis)
  - [x] Added path finding, caller/callee traversal, impact analysis
  - [x] Added cross-language relationship queries
  - [x] Added DI service collection extensions
  - [x] Created Ast.GraphDb.UnitTests (35 tests passing)
  - [x] Added Neo4j.Driver package to Directory.Packages.props
- [x] **Phase 5: Query Interface with Fluent API**
  - [x] Created Ast.Query project
  - [x] Implemented ICodeQuery fluent interface
  - [x] Implemented CodeQueryBuilder with chained methods
  - [x] Implemented HybridQueryService (graph + semantic search)
  - [x] Supported query types: nodes, paths, callers, callees, impact
  - [x] Added filter support (language, type, file, name pattern)
  - [x] Added DI service collection extensions
  - [x] Created Ast.Query.UnitTests (52 tests passing)
- [x] **Phase 5.5: Graph Database Integration Tests**
  - [x] Created docker-compose.yml for Neo4j + Qdrant
  - [x] Created Ast.GraphDb.IntegrationTests project
  - [x] Created Neo4jDockerFixture for Docker management
  - [x] Parses sample repos (C# and PowerShell from Ast.IntegrationTests)
  - [x] Indexes hypergraph data into Neo4j
  - [x] Validates using fluent query API
  - [x] Neo4j remains running after tests for manual validation
- [x] **Phase 6A: LSP Integration**
  - [x] Created Ast.Lsp project with LSP client implementation
  - [x] Implemented ILspClient interface for language server communication
  - [x] Implemented LspClient using StreamJsonRpc over stdio
  - [x] Implemented LspServerManager for server lifecycle management
  - [x] Implemented LspEnrichmentService for HypergraphNode enrichment
  - [x] Added LSP protocol models (Position, Range, Location, HoverResult, etc.)
  - [x] Added configuration classes (LspConfiguration, CSharpLspConfig, PowerShellLspConfig)
  - [x] Added DI service collection extensions
  - [x] Enhanced HypergraphNode with GetEnhancedEmbeddingText() and IsLspEnriched
  - [x] Created Ast.Lsp.UnitTests (56 tests passing)
- [x] **Phase 6B: LSP-Embedding Pipeline Integration**
  - [x] Added Ast.Lsp project reference to Ast.Embedding
  - [x] Created EnrichedEmbeddingPipeline orchestrating LSP enrichment + embedding generation
  - [x] Created EnrichedCodeEmbedding model with LSP metadata (signature, documentation, callers, callees)
  - [x] Created EnrichedEmbeddingOptions for pipeline configuration
  - [x] Created EnrichedEmbeddingResult for pipeline output with timing and statistics
  - [x] Created PipelineProgress for multi-step progress reporting
  - [x] Added DI extensions: AddEnrichedEmbeddingPipeline, AddCSharpEnrichedEmbedding, AddPowerShellEnrichedEmbedding, AddFullEnrichedEmbedding, AddEmbeddingOnlyPipeline
  - [x] Updated Ast.Embedding.UnitTests (65 tests passing, +34 new tests)
- [x] **Phase 6C: CLI Application**
  - [x] Created Ast.Cli project with Spectre.Console.Cli
  - [x] Implemented ParseCommand - Parses repositories and generates hypergraph JSON
  - [x] Implemented EnrichCommand - Enriches hypergraph with LSP semantic information
  - [x] Implemented EmbedCommand - Generates embeddings from hypergraph
  - [x] Implemented IndexCommand - Indexes data into Neo4j and Qdrant
  - [x] Implemented QueryCommand - Queries indexed data (semantic, callers, callees, path, impact)
  - [x] Implemented PipelineCommand - Full pipeline: parse → enrich → embed → index
  - [x] Implemented StatsCommand - Shows statistics about hypergraph or databases
  - [x] Added progress reporting with Spectre.Console
  - [x] Added JSON and table output formats

### In Progress

- [ ] None - ready for Phase 7 (Integration Testing)

---

## Gap Analysis Summary

| Area | Current State | Target State | Gap |
|------|--------------|--------------|-----|
| **C# Parsing** | Roslyn-based, 1048 lines | Complete | None |
| **PowerShell Parsing** | PS SDK-based, 815 lines | Complete | None |
| **Hypergraph Models** | Full model with 32 edge types | Complete | None |
| **Serialization** | JSON + HyperNetX export | Complete | None |
| **Graph Database** | Neo4j with Cypher queries | Complete | None |
| **Vector Database** | Qdrant integration with semantic search | Complete | None |
| **Embeddings** | LLamaSharp + HTTP API support | Complete | None |
| **Query Interface** | Fluent API + Hybrid Search | Complete | None |
| **LSP Integration** | StreamJsonRpc client (56 tests) | Complete | None |
| **LSP-Embedding Pipeline** | Enriched pipeline (34 tests) | Complete | None |
| **CLI Tool** | 7 commands (parse, enrich, embed, index, query, pipeline, stats) | Complete | None |
| **Unit Tests** | 437 tests passing | BDD test coverage | Expanding coverage |
| **Integration Tests** | 11 tests (C# + PowerShell repos) | E2E test scenarios | Partial - need DB integration tests |

---

## Technology Stack

### Selected

| Component | Technology | Version | NuGet Package |
|-----------|------------|---------|---------------|
| Graph DB | Neo4j | 5.26 | `Neo4j.Driver` 5.28.3 |
| Vector DB | Qdrant | Latest | `Qdrant.Client` 1.12.0 |
| Local LLM | LLamaSharp | 0.18.0 | `LLamaSharp` + Backend |
| Embedding Model | Qwen2.5-Coder | 0.5B | GGUF format |
| LSP Client | StreamJsonRpc | 2.17.8 | `StreamJsonRpc` |
| CLI Framework | Spectre.Console | 0.53.0 | `Spectre.Console.Cli` |

### Existing (Reuse)

| Component | Technology | Notes |
|-----------|------------|-------|
| C# Parser | Roslyn | `Microsoft.CodeAnalysis.CSharp` 4.8.0 |
| PS Parser | PowerShell SDK | `Microsoft.PowerShell.SDK` 7.4.0 |
| Testing | Reqnroll + MSTest | BDD framework |
| Logging | Serilog | Console + File sinks |
| DI | Microsoft.Extensions | Standard .NET DI |

---

## Implementation Phases

### Phase 1: Foundation & Testing (COMPLETED)
- [x] Created unit test projects for existing code (166 tests total)
- [x] Added configuration system (AstConfiguration + nested configs)
- [x] Created integration tests for real repositories
- Completed: 2026-01-21

### Phase 2: Embedding Generation (COMPLETED)
- [x] Created Ast.Embedding project
- [x] Integrated LLamaSharp for local GGUF models
- [x] Implemented HttpEmbeddingGenerator for Ollama/OpenAI APIs
- [x] Implemented CodeEmbeddingService with similarity search
- [x] Created unit tests (31 initial, 65 total after Phase 6B)
- Completed: 2026-01-21

### Phase 3: Vector Database (COMPLETED)
- [x] Created Ast.VectorDb project
- [x] Integrated Qdrant via gRPC client
- [x] Implemented IVectorStore abstraction
- [x] Implemented CodeVectorService for semantic search
- [x] Created 28 unit tests
- Completed: 2026-01-21

### Phase 4: Graph Database (COMPLETED)
- [x] Created Ast.GraphDb project
- [x] Integrated Neo4j via official driver
- [x] Implemented IGraphStore abstraction
- [x] Implemented Neo4jGraphStore with full Cypher query support
- [x] Implemented GraphQueryService for high-level code analysis
- [x] Created 35 unit tests
- Completed: 2026-01-21

### Phase 5: Query Interface (COMPLETED)
- [x] Created Ast.Query project
- [x] Implemented fluent query API (ICodeQuery, INodeQuery, etc.)
- [x] Implemented CodeQueryBuilder with chained methods
- [x] Implemented HybridQueryService for combined graph + semantic search
- [x] Created 52 unit tests
- Completed: 2026-01-21

### Phase 5.5: Graph Database Integration Tests (COMPLETED)
- [x] Created docker-compose.yml for Neo4j + Qdrant infrastructure
- [x] Created Ast.GraphDb.IntegrationTests project
- [x] Created Neo4jDockerFixture for automatic Docker management
- [x] Integration tests parse sample repos and index into Neo4j
- [x] Validation using fluent query API
- [x] Neo4j remains running after tests for manual inspection
- Completed: 2026-01-21

### Phase 6A: LSP Integration (COMPLETED)
- [x] Created Ast.Lsp project for Language Server Protocol integration
- [x] Implemented ILspClient interface with all LSP operations (hover, definitions, references, etc.)
- [x] Implemented LspClient using StreamJsonRpc for JSON-RPC over stdio
- [x] Implemented LspServerManager for language server lifecycle management
- [x] Implemented LspEnrichmentService for enhancing HypergraphNodes with LSP data
- [x] Added configuration classes for C# (csharp-ls, OmniSharp) and PowerShell (PSES) servers
- [x] Enhanced HypergraphNode with GetEnhancedEmbeddingText() for LSP-enriched embedding
- [x] Created Ast.Lsp.UnitTests with 56 tests
- [x] Created LSP_MIGRATION_PLAN.md documenting the migration strategy
- Completed: 2026-01-23

### Phase 6B: LSP-Embedding Pipeline Integration (COMPLETED)
- [x] Added Ast.Lsp as project reference to Ast.Embedding
- [x] Created EnrichedEmbeddingPipeline class orchestrating LSP enrichment and embedding generation
- [x] Created EnrichedCodeEmbedding model extending CodeEmbedding with LSP metadata
- [x] Created EnrichedEmbeddingOptions for configuring pipeline behavior
- [x] Created EnrichedEmbeddingResult for pipeline output with statistics
- [x] Created PipelineProgress for multi-step progress reporting (LSP + embedding)
- [x] Added DI extensions for common configurations (C#, PowerShell, both, or embedding-only)
- [x] Created 34 new unit tests (total 65 in Ast.Embedding.UnitTests)
- Completed: 2026-01-23

### Phase 6C: CLI Application (COMPLETED)
- [x] Created Ast.Cli project with Spectre.Console.Cli
- [x] Implemented 7 commands: parse, enrich, embed, index, query, pipeline, stats
- [x] Added progress reporting with Spectre.Console
- [x] Added JSON and table output formats
- Completed: 2026-01-23

### Phase 7: Integration Testing (Not Started)
- Create integration test project
- E2E test scenarios with LSP servers
- Performance benchmarks
- Estimated: 2 weeks

---

## Project Structure

```
src/ast/
├── Ast.Core/                    # [EXISTS] Core models
│   ├── HypergraphModels.cs      # [EXISTS] Node, edge, graph models
│   ├── HypergraphSerializer.cs  # [EXISTS] JSON serialization
│   ├── RepositoryProcessor.cs   # [EXISTS] Multi-language processing
│   └── AstConfiguration.cs      # [EXISTS] Configuration options
│
├── Ast.Csharp/                  # [EXISTS] C# parser
│   ├── AstNode.cs               # [EXISTS]
│   ├── CsharpAstParser.cs       # [EXISTS]
│   └── CSharpHyperedgeExtractor.cs  # [EXISTS] 1048 lines
│
├── Ast.PowerShell/              # [EXISTS] PowerShell parser
│   ├── PsAstNode.cs             # [EXISTS]
│   ├── PowerShellAstParser.cs   # [EXISTS]
│   └── PowerShellHyperedgeExtractor.cs  # [EXISTS] 815 lines
│
├── Ast.Embedding/               # [EXISTS] LLM embeddings
│   ├── IEmbeddingGenerator.cs   # [EXISTS] Generator interface
│   ├── LLamaEmbeddingGenerator.cs  # [EXISTS] Local GGUF via LLamaSharp
│   ├── HttpEmbeddingGenerator.cs   # [EXISTS] Ollama/OpenAI API
│   ├── CodeEmbeddingService.cs  # [EXISTS] High-level service
│   ├── EmbeddingModels.cs       # [EXISTS] Data models
│   ├── EmbeddingGeneratorFactory.cs  # [EXISTS] Factory
│   └── ServiceCollectionExtensions.cs  # [EXISTS] DI extensions
│
├── Ast.GraphDb/                 # [EXISTS] Neo4j integration
│   ├── IGraphStore.cs           # [EXISTS] Store interface
│   ├── GraphModels.cs           # [EXISTS] Data models
│   ├── Neo4jGraphStore.cs       # [EXISTS] Neo4j implementation
│   ├── GraphQueryService.cs     # [EXISTS] High-level service
│   └── ServiceCollectionExtensions.cs  # [EXISTS] DI extensions
│
├── Ast.VectorDb/                # [EXISTS] Qdrant integration
│   ├── IVectorStore.cs          # [EXISTS] Store interface
│   ├── VectorModels.cs          # [EXISTS] Data models
│   ├── QdrantVectorStore.cs     # [EXISTS] Qdrant implementation
│   ├── CodeVectorService.cs     # [EXISTS] High-level service
│   └── ServiceCollectionExtensions.cs  # [EXISTS] DI extensions
│
├── Ast.Query/                   # [EXISTS] Query service with fluent API
│   ├── ICodeQuery.cs            # [EXISTS] Fluent interface definitions
│   ├── QueryModels.cs           # [EXISTS] Data models
│   ├── CodeQueryBuilder.cs      # [EXISTS] Fluent query builder
│   ├── HybridQueryService.cs    # [EXISTS] Hybrid search service
│   └── ServiceCollectionExtensions.cs  # [EXISTS] DI extensions
│
├── Ast.Lsp/                     # [EXISTS] LSP integration
│   ├── ILspClient.cs            # [EXISTS] LSP client interface
│   ├── LspClient.cs             # [EXISTS] StreamJsonRpc implementation
│   ├── LspModels.cs             # [EXISTS] LSP protocol models
│   ├── LspConfiguration.cs      # [EXISTS] Configuration classes
│   ├── LspServerManager.cs      # [EXISTS] Server lifecycle management
│   ├── LspEnrichmentService.cs  # [EXISTS] HypergraphNode enrichment
│   └── ServiceCollectionExtensions.cs  # [EXISTS] DI extensions
│
├── Ast.Cli/                     # [EXISTS] CLI application
│   ├── Program.cs               # [EXISTS] Main entry point
│   ├── Ast.Cli.csproj           # [EXISTS] Project file
│   └── Commands/                # [EXISTS] 7 commands
│       ├── ParseCommand.cs      # [EXISTS] Repository parsing
│       ├── EnrichCommand.cs     # [EXISTS] LSP enrichment
│       ├── EmbedCommand.cs      # [EXISTS] Embedding generation
│       ├── IndexCommand.cs      # [EXISTS] Database indexing
│       ├── QueryCommand.cs      # [EXISTS] Semantic/graph queries
│       ├── PipelineCommand.cs   # [EXISTS] Full pipeline
│       └── StatsCommand.cs      # [EXISTS] Statistics display
│
├── docker-compose.yml           # [EXISTS] Neo4j + Qdrant infrastructure
│
├── Ast.Core.UnitTests/          # [EXISTS] 51 tests
├── Ast.Csharp.UnitTests/        # [EXISTS] 62 tests
├── Ast.PowerShell.UnitTests/    # [EXISTS] 53 tests
├── Ast.Embedding.UnitTests/     # [EXISTS] 65 tests
├── Ast.VectorDb.UnitTests/      # [EXISTS] 28 tests
├── Ast.GraphDb.UnitTests/       # [EXISTS] 35 tests
├── Ast.Query.UnitTests/         # [EXISTS] 52 tests
├── Ast.Lsp.UnitTests/           # [EXISTS] 56 tests
├── Ast.IntegrationTests/        # [EXISTS] 11 tests
└── Ast.GraphDb.IntegrationTests/ # [EXISTS] Neo4j integration tests
```

---

## Dependencies

### Already Added to `Directory.Packages.props`:

```xml
<!-- Embedding / Local LLM (Phase 2 - DONE) -->
<PackageVersion Include="LLamaSharp" Version="0.18.0" />
<PackageVersion Include="LLamaSharp.Backend.Cpu" Version="0.18.0" />
<PackageVersion Include="LLamaSharp.Backend.Cuda12" Version="0.18.0" />

<!-- Vector Database (Phase 3 - DONE) -->
<PackageVersion Include="Qdrant.Client" Version="1.12.0" />

<!-- Graph Database (Phase 4 - DONE) -->
<PackageVersion Include="Neo4j.Driver" Version="5.28.0" />

<!-- LSP Client (Phase 6A - DONE) -->
<PackageVersion Include="StreamJsonRpc" Version="2.17.8" />

<!-- CLI (Phase 6C) -->
<PackageVersion Include="Spectre.Console.Cli" Version="0.53.0" />
```

---

## Docker Infrastructure

```yaml
# docker-compose.yml for development
version: '3.8'
services:
  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"
      - "6334:6334"
    volumes:
      - qdrant_data:/qdrant/storage

  neo4j:
    image: neo4j:5.26-community
    ports:
      - "7474:7474"
      - "7687:7687"
    environment:
      - NEO4J_AUTH=neo4j/password123
    volumes:
      - neo4j_data:/data

volumes:
  qdrant_data:
  neo4j_data:
```

---

## Risk Register

| Risk | Impact | Probability | Mitigation |
|------|--------|-------------|------------|
| LLamaSharp CUDA issues | High | Medium | Fallback to CPU backend or Ollama |
| Neo4j performance at scale | Medium | Low | Add indexes, caching layer |
| Embedding quality for code | Medium | Medium | Test multiple models, fine-tune if needed |
| Memory usage with large repos | High | Medium | Streaming, batching, GC management |

---

## Next Steps

1. **Phase 7**: Full end-to-end integration tests with LSP servers and databases
2. **Performance**: Benchmark and optimize for large repositories
3. **Documentation**: Add user guides and API documentation
4. **Polish**: Add CLI unit tests, improve error handling

---

## Questions for User

1. Do you have a GPU available for LLamaSharp CUDA backend?
2. Preferred location for GGUF model files?
3. Any specific Neo4j/Qdrant deployment requirements (Docker vs. cloud)?
4. Should the CLI support Windows services or just console mode?

---

## Reference Links

- Implementation Plan: [AST_IMPLEMENTATION_PLAN.md](./AST_IMPLEMENTATION_PLAN.md)
- LSP Migration Plan: [LSP_MIGRATION_PLAN.md](./LSP_MIGRATION_PLAN.md)
- LSP Deep Dive: [lsp-deep-dive.md](./lsp-deep-dive.md)
- Decision Log: [AST_DECISION_LOG.md](./AST_DECISION_LOG.md)
- Inspiration: [HyperGraphReasoning](https://github.com/smartpcr/hypergraphreasoning)
