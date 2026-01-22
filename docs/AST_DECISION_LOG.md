# AST HyperGraph Reasoning - Decision Log

This document tracks all architectural decisions, design choices, and implementation decisions for the AST HyperGraph Reasoning system.

---

## Decision Record Format

Each decision follows this format:
- **ID**: Unique identifier (AST-NNN)
- **Date**: When the decision was made
- **Status**: Proposed | Accepted | Deprecated | Superseded
- **Context**: Why we needed to make this decision
- **Decision**: What we decided
- **Consequences**: Impact of the decision
- **Alternatives Considered**: What else we evaluated

---

## Decisions

### AST-001: Graph Database Selection

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Need a graph database to store hypergraph structure for code relationships (inheritance, method calls, data flow). Must support .NET, have good performance, and handle millions of nodes. |
| **Decision** | **Neo4j** with `Neo4j.Driver` v5.28.3 NuGet package |
| **Consequences** | - Property graph model requires modeling hyperedges as intermediate nodes<br>- Cypher query language for traversals<br>- Need to run Neo4j container for development<br>- Well-documented, large community |
| **Alternatives Considered** | 1. **Azure Cosmos DB (Gremlin API)** - Good for Azure integration but vendor lock-in<br>2. **Apache TinkerPop** - More complex setup<br>3. **ArangoDB** - Multi-model but smaller .NET community |

**Rationale**: Neo4j is the industry standard for graph databases with excellent .NET support. The hyperedge modeling pattern (hyperedge as node connected to participating nodes) is well-established.

---

### AST-002: Vector Database Selection

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Need a vector database to store code embeddings for semantic search. Must support filtering, have good .NET client, and be easy to self-host. |
| **Decision** | **Qdrant** with `Qdrant.Client` v1.12.0 NuGet package |
| **Consequences** | - Rust-based, high performance<br>- Good filtering/hybrid search support<br>- Integrates with Microsoft Semantic Kernel<br>- Docker deployment for development |
| **Alternatives Considered** | 1. **Milvus** - More mature, but heavier weight<br>2. **Pinecone** - SaaS only, no self-hosting<br>3. **Chroma** - Simpler but less performant<br>4. **pgvector** - Would require PostgreSQL dependency |

**Rationale**: Qdrant offers the best balance of performance, features, and .NET integration. The Semantic Kernel connector provides a standardized abstraction layer.

---

### AST-003: Local LLM/Embedding Library Selection

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted (Updated) |
| **Context** | Need local LLM inference for generating code embeddings without cloud API dependency. User specified llama.cpp with Qwen Coder model. Models already downloaded and served via endpoint. |
| **Decision** | **HTTP Client abstraction** - consume embedding API via endpoint URL |
| **Consequences** | - Decoupled from specific LLM runtime<br>- Works with Ollama, llama.cpp server, or any OpenAI-compatible endpoint<br>- Simple HttpClient-based implementation<br>- GPU/model management handled externally |
| **Alternatives Considered** | 1. **LLamaSharp** - Direct binding, but user prefers external serving<br>2. **Semantic Kernel** - Too heavy for just embeddings |

**Rationale**: User already has models served via endpoint. HTTP abstraction provides flexibility to switch backends without code changes. Interface pattern allows easy mocking for tests.

---

### AST-004: Embedding Model Selection

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Proposed |
| **Context** | Need to choose an embedding model optimized for code understanding. Model must be available in GGUF format and produce meaningful embeddings for code constructs. |
| **Decision** | Primary: **qwen2.5-coder-0.5b-instruct** (code-specific)<br>Fallback: **nomic-embed-text-v1.5** (general purpose) |
| **Consequences** | - Qwen Coder understands code semantics better<br>- 896 dimensions for Qwen, 768 for Nomic<br>- ~500MB model size each<br>- May need to test quality on actual codebase |
| **Alternatives Considered** | 1. **bge-small-en** - Smaller (384 dim) but less accurate<br>2. **CodeBERT** - Not available in GGUF<br>3. **OpenAI text-embedding-ada-002** - Cloud-only |

**Rationale**: Qwen Coder is specifically trained on code and understands programming concepts. Nomic provides a fallback for general text in comments/documentation.

---

### AST-005: Hyperedge-to-Property-Graph Mapping

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Neo4j is a property graph database, not a native hypergraph database. Need a mapping strategy to represent hyperedges (relationships connecting multiple nodes) in Neo4j. |
| **Decision** | Model hyperedges as intermediate nodes with participation relationships |
| **Consequences** | - Hyperedge becomes a node with label `:Hyperedge`<br>- Participating nodes connect via `[:PARTICIPATES_IN]` relationship<br>- Role stored on relationship property<br>- Slightly more complex queries |
| **Pattern** | See diagram below |

```
Hypergraph:                       Neo4j Model:

MethodCall(caller, callee, args)  (caller:CodeNode)-[:PARTICIPATES_IN {role:'caller'}]->(e:Hyperedge {type:'MethodCall'})
                                  (callee:CodeNode)-[:PARTICIPATES_IN {role:'callee'}]->(e)
                                  (arg1:CodeNode)-[:PARTICIPATES_IN {role:'arg_0'}]->(e)
```

**Rationale**: This is the standard pattern for modeling hypergraphs in property graphs. Preserves the n-ary nature of relationships while leveraging Neo4j's query capabilities.

---

### AST-006: Project Structure

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Need to organize the new projects for embedding, vector DB, graph DB, query, and CLI functionality. |
| **Decision** | Modular project structure with clear separation of concerns |

```
src/ast/
├── Ast.Core/           # Models, interfaces (existing)
├── Ast.Csharp/         # C# parser (existing)
├── Ast.PowerShell/     # PowerShell parser (existing)
├── Ast.Embedding/      # LLM embedding generation (NEW)
├── Ast.GraphDb/        # Neo4j persistence (NEW)
├── Ast.VectorDb/       # Qdrant storage (NEW)
├── Ast.Query/          # Unified query service (NEW)
├── Ast.Cli/            # Command-line tool (NEW)
└── Ast.*.UnitTests/    # Test projects (NEW)
```

**Rationale**: Each project has a single responsibility. Dependencies flow inward (Cli → Query → GraphDb/VectorDb → Core).

---

### AST-007: Testing Strategy

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Need consistent testing approach matching the existing codebase patterns (BDD with Reqnroll). |
| **Decision** | BDD testing with Reqnroll + MSTest, consistent with existing EtwIngest project |
| **Consequences** | - Feature files in `.feature` format<br>- Step definitions in `*Steps.cs`<br>- FluentAssertions for assertions<br>- Moq for mocking<br>- Docker containers for integration tests |

**Test Categories:**
- `@unit` - Fast unit tests, no external dependencies
- `@integration` - Requires Docker containers
- `@slow` - Long-running tests (>10 seconds)

---

### AST-008: Configuration Approach

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Current code has hardcoded settings (excluded directories, file patterns). Need configurable approach for production use. |
| **Decision** | Microsoft.Extensions.Options pattern with appsettings.json |

```csharp
public class AstConfiguration
{
    public string[] ExcludedDirectories { get; set; }
    public string[] CSharpFilePatterns { get; set; }
    public int MaxFileSizeBytes { get; set; }
    public EmbeddingConfiguration Embedding { get; set; }
    public GraphDbConfiguration GraphDb { get; set; }
    public VectorDbConfiguration VectorDb { get; set; }
}
```

**Rationale**: Consistent with existing .NET patterns, supports configuration binding, validation, and DI integration.

---

### AST-009: CLI Framework Selection

| Field | Value |
|-------|-------|
| **Date** | 2026-01-21 |
| **Status** | Accepted |
| **Context** | Need a CLI framework for the Ast.Cli project. Must support commands, arguments, options, and rich console output. |
| **Decision** | **Spectre.Console.Cli** (already used in codebase for ProgressTree) |
| **Consequences** | - Consistent with existing codebase<br>- Rich console output with progress bars<br>- Type-safe command definitions<br>- Automatic help generation |
| **Alternatives Considered** | 1. **System.CommandLine** - Microsoft official but more complex<br>2. **CommandLineParser** - Already in deps but less feature-rich<br>3. **Cocona** - Good but adds another dependency |

---

## Design Prompts & Rationale

### Prompt: Why use hypergraphs instead of regular graphs?

**Answer**: Regular graphs (pairwise edges) lose contextual information. Consider a method call `f(a, b, c)`:
- **Pairwise graph**: `f→a`, `f→b`, `f→c` - loses the fact that a, b, c are arguments to the SAME call
- **Hypergraph**: `{f, a, b, c}` with roles - preserves the complete relationship

This is critical for:
- Understanding method signatures completely
- Tracking data flow through function parameters
- Detecting patterns like dependency injection

### Prompt: Why separate Vector DB and Graph DB?

**Answer**: Different query patterns require different storage:
- **Vector DB (Qdrant)**: Optimized for "find similar things" - ANN search in high-dimensional space
- **Graph DB (Neo4j)**: Optimized for "traverse relationships" - path finding, pattern matching

A hybrid approach allows:
1. Semantic search: "Find code related to authentication" → Vector search
2. Structural queries: "What calls this method?" → Graph traversal
3. Combined: "Find authentication code that affects the login flow" → Both

### Prompt: Why LLamaSharp over cloud APIs?

**Answer**:
1. **Privacy**: Code never leaves the local machine
2. **Cost**: No per-token charges for large codebases
3. **Latency**: No network round-trips
4. **Offline**: Works without internet
5. **Control**: Can choose/fine-tune embedding model

Trade-off: Requires GPU for good performance, more setup complexity.

### Prompt: How to handle large repositories?

**Answer**: Streaming and batching strategy:
1. **Streaming parse**: Process files one at a time, don't load all ASTs in memory
2. **Batch embeddings**: Generate embeddings in batches of 32-64
3. **Incremental updates**: Only re-process changed files (track file hashes)
4. **Partitioning**: Store by repository/namespace for isolation

---

## Implementation Status

| Component | Status | Notes |
|-----------|--------|-------|
| Ast.Core | Implemented | Models, serialization complete |
| Ast.Csharp | Implemented | Parser and extractor complete (1048 lines) |
| Ast.PowerShell | Implemented | Parser and extractor complete (815 lines) |
| Ast.Embedding | Not Started | Phase 2 |
| Ast.VectorDb | Not Started | Phase 3 |
| Ast.GraphDb | Not Started | Phase 4 |
| Ast.Query | Not Started | Phase 5 |
| Ast.Cli | Not Started | Phase 6 |
| Unit Tests | Not Started | Phase 1 |
| Integration Tests | Not Started | Phase 7 |

---

## Open Questions

1. **Q**: Should we support incremental indexing (only changed files)?
   **A**: Yes, but as a Phase 2+ enhancement. First get full indexing working.

2. **Q**: How to handle binary/generated files?
   **A**: Exclude common patterns (bin/, obj/, node_modules/, *.Designer.cs)

3. **Q**: Should embeddings be persisted separately from vector DB?
   **A**: Store in vector DB as primary, but allow JSON export for portability.

4. **Q**: How to handle parsing errors?
   **A**: Log errors, create error nodes, continue processing. Don't fail entire repo for one bad file.

---

## Change History

| Date | Author      | Change |
|------|-------------|--------|
| 2026-01-21 | Xiaodong Li | Initial decision log created |
