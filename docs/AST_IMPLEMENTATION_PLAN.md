# HyperGraph Code Reasoning - Implementation Plan

## Executive Summary

This document outlines a phased implementation plan to transform the existing AST parsing infrastructure into a complete HyperGraph Code Reasoning system, inspired by [HyperGraphReasoning](https://github.com/smartpcr/hypergraphreasoning).

### Current State Analysis

**What's Implemented:**
- Hypergraph data models (`HypergraphNode`, `Hyperedge`, `HypergraphData`)
- C# AST parser using Roslyn (`CsharpAstParser`, `CSharpHyperedgeExtractor` - 1048 lines)
- PowerShell AST parser (`PowerShellAstParser`, `PowerShellHyperedgeExtractor` - 815 lines)
- Repository processor with cross-language reference detection
- JSON/HyperNetX serialization

**What's Missing (Gaps):**
| Gap | Priority | Description |
|-----|----------|-------------|
| Graph Database | High | No persistence layer for hypergraph structure |
| Vector Database | High | No embedding storage or semantic search |
| LLM/Embedding Integration | High | No local AI for generating embeddings |
| CLI Application | Medium | No driver program to use the system |
| Unit Tests | Medium | No test coverage for AST projects |
| Configuration System | Medium | Hardcoded settings |
| Query Interface | Low | No API to query the hypergraph |
| Additional Languages | Low | Only C# and PowerShell supported |

---

## Technology Recommendations

### Graph Database: **Neo4j**
- **NuGet**: `Neo4j.Driver` (v5.28.3)
- **Rationale**: Industry standard, excellent .NET support, Cypher query language, can model hypergraphs using intermediate nodes
- **Alternative**: Azure Cosmos DB with Gremlin API (if Azure integration preferred)

### Vector Database: **Qdrant**
- **NuGet**: `Qdrant.Client`
- **Rationale**: High performance, Rust-based, excellent .NET support via Semantic Kernel, hybrid search with filtering
- **Alternative**: Milvus (`Milvus.Client`) - good for larger scale deployments

### Local LLM/Embeddings: **LLamaSharp**
- **NuGet**: `LLamaSharp`, `LLamaSharp.Backend.Cuda12` (or CPU)
- **Model**: Qwen2.5-Coder (as specified by user)
- **Embedding Model**: `nomic-embed-text` or `bge-small-en` (384-1024 dimensions)
- **Rationale**: Native .NET binding to llama.cpp, supports GGUF models, embedding generation

---

## Project Structure (Final State)

```
src/ast/
├── Ast.Core/                    # Core models (existing)
├── Ast.Csharp/                  # C# parser (existing)
├── Ast.PowerShell/              # PowerShell parser (existing)
├── Ast.Embedding/               # NEW: LLM embedding generation
├── Ast.GraphDb/                 # NEW: Neo4j graph persistence
├── Ast.VectorDb/                # NEW: Qdrant vector storage
├── Ast.Query/                   # NEW: Query interface
├── Ast.Cli/                     # NEW: Command-line application
├── Ast.Core.UnitTests/          # NEW: Unit tests
├── Ast.Csharp.UnitTests/        # NEW: Unit tests
├── Ast.PowerShell.UnitTests/    # NEW: Unit tests
├── Ast.Embedding.UnitTests/     # NEW: Unit tests
├── Ast.GraphDb.UnitTests/       # NEW: Unit tests
├── Ast.VectorDb.UnitTests/      # NEW: Unit tests
└── Ast.IntegrationTests/        # NEW: Integration tests
```

---

## Phase 1: Foundation & Testing (Week 1-2)

### 1.1 Create Unit Test Projects

**Objective**: Establish test coverage for existing code before adding new features.

**Tasks:**
1. Create `Ast.Core.UnitTests` project
   - Test `HypergraphNode` equality, hashing, embedding text generation
   - Test `Hyperedge` creation, node relationships, cardinality
   - Test `HypergraphData` merge operations
   - Test `HypergraphSerializer` JSON round-trip, HyperNetX export

2. Create `Ast.Csharp.UnitTests` project
   - Test `CsharpAstParser.ParseCode()` with various C# constructs
   - Test `CSharpHyperedgeExtractor` edge extraction for each `HyperedgeType`
   - Test nested class handling
   - Test generic type parameters
   - Test XML documentation extraction

3. Create `Ast.PowerShell.UnitTests` project
   - Test `PowerShellAstParser.ParseCode()` with PS constructs
   - Test `PowerShellHyperedgeExtractor` for cmdlet calls, pipelines, DSC
   - Test parameter extraction
   - Test help comment parsing

**Test Structure (BDD with Reqnroll):**
```gherkin
Feature: C# Hyperedge Extraction

  @unit @csharp
  Scenario: Extract class inheritance hyperedge
    Given the following C# code:
      """
      public class Dog : Animal, IBarking { }
      """
    When I extract hyperedges
    Then I should have a hyperedge of type "ClassDefinition"
    And the hyperedge should contain nodes "Dog", "Animal", "IBarking"
    And node "Dog" should have role "defined_class"
    And nodes "Animal", "IBarking" should have role "base_type"
```

**Deliverables:**
- [ ] `Ast.Core.UnitTests.csproj` with 20+ test cases
- [ ] `Ast.Csharp.UnitTests.csproj` with 30+ test cases
- [ ] `Ast.PowerShell.UnitTests.csproj` with 20+ test cases
- [ ] All tests passing

---

### 1.2 Add Configuration System

**Objective**: Replace hardcoded settings with configurable options.

**Tasks:**
1. Create `AstConfiguration.cs` in `Ast.Core`:
   ```csharp
   public class AstConfiguration
   {
       public string[] ExcludedDirectories { get; set; } = ["bin", "obj", ".git", "node_modules"];
       public string[] CSharpFilePatterns { get; set; } = ["*.cs"];
       public string[] PowerShellFilePatterns { get; set; } = ["*.ps1", "*.psm1", "*.psd1"];
       public int MaxFileSizeBytes { get; set; } = 1_000_000;
       public bool EnableDataFlowAnalysis { get; set; } = true;
       public bool EnableCrossLanguageDetection { get; set; } = true;
   }
   ```

2. Update `RepositoryProcessor` to use configuration
3. Add `Microsoft.Extensions.Options` support

**Deliverables:**
- [ ] `AstConfiguration.cs` with all settings
- [ ] `RepositoryProcessor` refactored to use configuration
- [ ] Unit tests for configuration validation

---

## Phase 2: Embedding Generation (Week 3-4)

### 2.1 Create Ast.Embedding Project

**Objective**: Generate vector embeddings for code constructs using local LLM.

**Dependencies:**
```xml
<PackageReference Include="LLamaSharp" Version="0.18.0" />
<PackageReference Include="LLamaSharp.Backend.Cuda12" Version="0.18.0" />
<!-- OR for CPU: LLamaSharp.Backend.Cpu -->
```

**Core Interfaces:**
```csharp
public interface IEmbeddingGenerator : IDisposable
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int EmbeddingDimensions { get; }
}

public interface ICodeEmbeddingService
{
    Task<NodeEmbedding> EmbedNodeAsync(HypergraphNode node, CancellationToken ct = default);
    Task<EdgeEmbedding> EmbedEdgeAsync(Hyperedge edge, CancellationToken ct = default);
    Task<HypergraphEmbeddings> EmbedGraphAsync(HypergraphData graph, CancellationToken ct = default);
}
```

**Implementation:**
```csharp
public class LLamaEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly LLamaWeights _weights;
    private readonly LLamaEmbedder _embedder;

    public LLamaEmbeddingGenerator(string modelPath, int contextSize = 2048)
    {
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            EmbeddingMode = true
        };
        _weights = LLamaWeights.LoadFromFile(parameters);
        _embedder = new LLamaEmbedder(_weights, parameters);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct)
    {
        return await Task.Run(() => _embedder.GetEmbeddings(text), ct);
    }
}
```

**Model Recommendations:**
| Model | Dimensions | Size | Use Case |
|-------|------------|------|----------|
| `nomic-embed-text-v1.5.Q8_0.gguf` | 768 | 547MB | General purpose, good accuracy |
| `bge-small-en-v1.5.Q8_0.gguf` | 384 | 134MB | Fast, lower memory |
| `qwen2.5-coder-0.5b-instruct.Q8_0.gguf` | 896 | 531MB | Code-specific understanding |

**Embedding Strategy:**
1. **Node Embedding**: Use `GetEmbeddingText()` method on `HypergraphNode`
2. **Edge Embedding**: Concatenate node texts with relationship context
3. **Batch Processing**: Process nodes in batches of 32 for efficiency

**Deliverables:**
- [ ] `Ast.Embedding` project with interfaces and implementations
- [ ] `LLamaEmbeddingGenerator` implementation
- [ ] `CodeEmbeddingService` for graph embedding
- [ ] Batch processing support
- [ ] Unit tests with mock embedder
- [ ] Integration tests with actual model

---

### 2.2 Embedding Data Models

**Add to Ast.Core:**
```csharp
public class NodeEmbedding
{
    public string NodeId { get; set; }
    public float[] Vector { get; set; }
    public string EmbeddingModel { get; set; }
    public DateTime GeneratedAt { get; set; }
}

public class EdgeEmbedding
{
    public string EdgeId { get; set; }
    public float[] Vector { get; set; }
    public string EmbeddingModel { get; set; }
}

public class HypergraphEmbeddings
{
    public Dictionary<string, NodeEmbedding> NodeEmbeddings { get; set; }
    public Dictionary<string, EdgeEmbedding> EdgeEmbeddings { get; set; }
    public string RepositoryPath { get; set; }
    public DateTime GeneratedAt { get; set; }
}
```

---

## Phase 3: Vector Database Integration (Week 5-6)

### 3.1 Create Ast.VectorDb Project

**Objective**: Store and search embeddings using Qdrant vector database.

**Dependencies:**
```xml
<PackageReference Include="Qdrant.Client" Version="1.12.0" />
```

**Core Interfaces:**
```csharp
public interface IVectorStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertNodeEmbeddingAsync(NodeEmbedding embedding, HypergraphNode node, CancellationToken ct = default);
    Task UpsertEdgeEmbeddingAsync(EdgeEmbedding embedding, Hyperedge edge, CancellationToken ct = default);
    Task<IReadOnlyList<SimilarNode>> SearchSimilarNodesAsync(float[] queryVector, int topK = 10, NodeFilter? filter = null, CancellationToken ct = default);
    Task<IReadOnlyList<SimilarEdge>> SearchSimilarEdgesAsync(float[] queryVector, int topK = 10, EdgeFilter? filter = null, CancellationToken ct = default);
    Task DeleteByRepositoryAsync(string repositoryPath, CancellationToken ct = default);
}

public record SimilarNode(string NodeId, float Score, HypergraphNode Node);
public record SimilarEdge(string EdgeId, float Score, Hyperedge Edge);

public class NodeFilter
{
    public NodeType? Type { get; set; }
    public string? Language { get; set; }
    public string? RepositoryPath { get; set; }
}
```

**Qdrant Implementation:**
```csharp
public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _nodesCollection = "code_nodes";
    private readonly string _edgesCollection = "code_edges";
    private readonly int _vectorSize;

    public QdrantVectorStore(string host = "localhost", int port = 6334, int vectorSize = 768)
    {
        _client = new QdrantClient(host, port);
        _vectorSize = vectorSize;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Create collections with proper schema
        await _client.CreateCollectionAsync(_nodesCollection, new VectorParams
        {
            Size = (ulong)_vectorSize,
            Distance = Distance.Cosine
        }, cancellationToken: ct);

        // Create payload indexes for filtering
        await _client.CreatePayloadIndexAsync(_nodesCollection, "node_type", PayloadSchemaType.Keyword, ct);
        await _client.CreatePayloadIndexAsync(_nodesCollection, "language", PayloadSchemaType.Keyword, ct);
        await _client.CreatePayloadIndexAsync(_nodesCollection, "repository", PayloadSchemaType.Keyword, ct);
    }

    public async Task<IReadOnlyList<SimilarNode>> SearchSimilarNodesAsync(
        float[] queryVector, int topK, NodeFilter? filter, CancellationToken ct)
    {
        var filterConditions = BuildFilter(filter);
        var results = await _client.SearchAsync(_nodesCollection, queryVector,
            limit: (ulong)topK, filter: filterConditions, cancellationToken: ct);

        return results.Select(r => new SimilarNode(
            r.Id.ToString(),
            r.Score,
            DeserializeNode(r.Payload)
        )).ToList();
    }
}
```

**Collection Schema:**
```
nodes_collection:
  - id: string (node_id)
  - vector: float[768]
  - payload:
    - node_type: string
    - name: string
    - language: string
    - file_path: string
    - repository: string
    - line_number: int
    - metadata: json

edges_collection:
  - id: string (edge_id)
  - vector: float[768]
  - payload:
    - edge_type: string
    - node_ids: string[]
    - repository: string
    - file_path: string
```

**Deliverables:**
- [ ] `Ast.VectorDb` project
- [ ] `IVectorStore` interface
- [ ] `QdrantVectorStore` implementation
- [ ] Filtering and hybrid search support
- [ ] Unit tests with in-memory mock
- [ ] Integration tests with Docker Qdrant

---

### 3.2 Docker Compose for Development

```yaml
# docker-compose.yml
version: '3.8'
services:
  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"  # REST API
      - "6334:6334"  # gRPC
    volumes:
      - qdrant_data:/qdrant/storage
    environment:
      - QDRANT__SERVICE__GRPC_PORT=6334

volumes:
  qdrant_data:
```

---

## Phase 4: Graph Database Integration (Week 7-8)

### 4.1 Create Ast.GraphDb Project

**Objective**: Persist hypergraph structure to Neo4j for graph queries and traversal.

**Dependencies:**
```xml
<PackageReference Include="Neo4j.Driver" Version="5.28.3" />
```

**Hypergraph to Property Graph Mapping:**

Since Neo4j is a property graph (not hypergraph), we model hyperedges as intermediate nodes:

```
Hypergraph:                    Neo4j Property Graph:

  e1: {A, B, C}       →       (A)-[:IN_EDGE]->(e1:Hyperedge)
                              (B)-[:IN_EDGE]->(e1:Hyperedge)
                              (C)-[:IN_EDGE]->(e1:Hyperedge)
```

**Core Interfaces:**
```csharp
public interface IGraphStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);

    // Write operations
    Task UpsertNodeAsync(HypergraphNode node, CancellationToken ct = default);
    Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct = default);
    Task UpsertGraphAsync(HypergraphData graph, CancellationToken ct = default);

    // Read operations
    Task<HypergraphNode?> GetNodeByIdAsync(string nodeId, CancellationToken ct = default);
    Task<Hyperedge?> GetEdgeByIdAsync(string edgeId, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> GetNodesByTypeAsync(NodeType type, CancellationToken ct = default);
    Task<IReadOnlyList<Hyperedge>> GetEdgesForNodeAsync(string nodeId, CancellationToken ct = default);

    // Graph traversal
    Task<IReadOnlyList<HypergraphNode>> GetCallersOfAsync(string methodNodeId, int depth = 1, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> GetCalleesOfAsync(string methodNodeId, int depth = 1, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> GetInheritanceChainAsync(string classNodeId, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> GetDependenciesAsync(string nodeId, CancellationToken ct = default);

    // Delete operations
    Task DeleteByRepositoryAsync(string repositoryPath, CancellationToken ct = default);
}
```

**Neo4j Implementation:**
```csharp
public class Neo4jGraphStore : IGraphStore
{
    private readonly IDriver _driver;

    public Neo4jGraphStore(string uri, string user, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var session = _driver.AsyncSession();

        // Create indexes for performance
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("CREATE INDEX node_id IF NOT EXISTS FOR (n:CodeNode) ON (n.id)");
            await tx.RunAsync("CREATE INDEX node_type IF NOT EXISTS FOR (n:CodeNode) ON (n.type)");
            await tx.RunAsync("CREATE INDEX edge_id IF NOT EXISTS FOR (e:Hyperedge) ON (e.id)");
            await tx.RunAsync("CREATE INDEX repo IF NOT EXISTS FOR (n:CodeNode) ON (n.repository)");
        }, ct);
    }

    public async Task UpsertEdgeAsync(Hyperedge edge, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession();

        await session.ExecuteWriteAsync(async tx =>
        {
            // Create or update hyperedge node
            await tx.RunAsync(@"
                MERGE (e:Hyperedge {id: $edgeId})
                SET e.type = $edgeType,
                    e.file_path = $filePath,
                    e.repository = $repository
            ", new { edgeId = edge.Id, edgeType = edge.Type.ToString(),
                     filePath = edge.FilePath, repository = edge.Repository });

            // Connect nodes to hyperedge with roles
            foreach (var nodeId in edge.NodeIds)
            {
                var role = edge.GetRoleForNode(nodeId);
                await tx.RunAsync(@"
                    MATCH (n:CodeNode {id: $nodeId})
                    MATCH (e:Hyperedge {id: $edgeId})
                    MERGE (n)-[r:PARTICIPATES_IN {role: $role}]->(e)
                ", new { nodeId, edgeId = edge.Id, role });
            }
        }, ct);
    }

    public async Task<IReadOnlyList<HypergraphNode>> GetCallersOfAsync(
        string methodNodeId, int depth, CancellationToken ct)
    {
        await using var session = _driver.AsyncSession();

        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(@"
                MATCH (caller:CodeNode)-[:PARTICIPATES_IN {role: 'caller'}]->(e:Hyperedge {type: 'MethodInvocation'})
                      -[:PARTICIPATES_IN {role: 'callee'}]-(callee:CodeNode {id: $methodId})
                RETURN caller
                LIMIT 100
            ", new { methodId = methodNodeId });

            return await cursor.ToListAsync(r => DeserializeNode(r["caller"].As<INode>()));
        }, ct);

        return result;
    }
}
```

**Cypher Query Examples:**
```cypher
// Find all methods that call a specific method
MATCH (caller:CodeNode)-[:PARTICIPATES_IN {role: 'caller'}]->(e:Hyperedge {type: 'MethodInvocation'})
      -[:PARTICIPATES_IN {role: 'callee'}]-(callee:CodeNode {name: 'ProcessData'})
RETURN caller.name, caller.file_path

// Find inheritance chain
MATCH path = (derived:CodeNode {name: 'MyClass'})-[:PARTICIPATES_IN*]->(e:Hyperedge {type: 'ClassDefinition'})-[:PARTICIPATES_IN*]-(base:CodeNode)
WHERE base <> derived
RETURN path

// Find all usages of a variable
MATCH (def:CodeNode)-[:PARTICIPATES_IN {role: 'defined'}]->(e1:Hyperedge {type: 'VariableDefinition'})
MATCH (use:CodeNode)-[:PARTICIPATES_IN {role: 'used'}]->(e2:Hyperedge {type: 'VariableUse'})
WHERE def.name = use.name AND def.scope = use.scope
RETURN def, use
```

**Deliverables:**
- [ ] `Ast.GraphDb` project
- [ ] `IGraphStore` interface
- [ ] `Neo4jGraphStore` implementation
- [ ] Cypher queries for common traversals
- [ ] Unit tests
- [ ] Integration tests with Docker Neo4j

---

### 4.2 Docker Compose Update

```yaml
# docker-compose.yml
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
      - "7474:7474"  # HTTP
      - "7687:7687"  # Bolt
    volumes:
      - neo4j_data:/data
    environment:
      - NEO4J_AUTH=neo4j/password123
      - NEO4J_PLUGINS=["apoc"]

volumes:
  qdrant_data:
  neo4j_data:
```

---

## Phase 5: Query Interface (Week 9-10)

### 5.1 Create Ast.Query Project

**Objective**: Provide unified query API combining graph traversal and semantic search.

**Core Interfaces:**
```csharp
public interface ICodeQueryService
{
    // Semantic search
    Task<IReadOnlyList<CodeSearchResult>> SearchByDescriptionAsync(
        string naturalLanguageQuery, int topK = 10, CancellationToken ct = default);

    Task<IReadOnlyList<CodeSearchResult>> FindSimilarCodeAsync(
        string codeSnippet, int topK = 10, CancellationToken ct = default);

    // Structural queries
    Task<IReadOnlyList<HypergraphNode>> FindMethodsCallingAsync(string methodName, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> FindImplementationsOfAsync(string interfaceName, CancellationToken ct = default);
    Task<IReadOnlyList<HypergraphNode>> FindUsagesOfTypeAsync(string typeName, CancellationToken ct = default);

    // Hybrid queries (combining semantic + structural)
    Task<IReadOnlyList<CodeSearchResult>> FindRelatedCodeAsync(
        string nodeId, RelationshipType[] relationships, float semanticWeight = 0.5f, CancellationToken ct = default);

    // Impact analysis
    Task<ImpactAnalysis> AnalyzeChangeImpactAsync(string nodeId, CancellationToken ct = default);
}

public record CodeSearchResult(
    HypergraphNode Node,
    float SemanticScore,
    float StructuralScore,
    float CombinedScore,
    string[] MatchReasons
);

public record ImpactAnalysis(
    HypergraphNode TargetNode,
    IReadOnlyList<HypergraphNode> DirectlyAffected,
    IReadOnlyList<HypergraphNode> IndirectlyAffected,
    IReadOnlyList<Hyperedge> AffectedRelationships
);
```

**Implementation Strategy:**
1. Convert natural language query to embedding
2. Search vector DB for semantically similar nodes
3. For each result, fetch structural context from graph DB
4. Re-rank results based on combined semantic + structural relevance

**Deliverables:**
- [ ] `Ast.Query` project
- [ ] `ICodeQueryService` interface
- [ ] `HybridQueryService` implementation
- [ ] Impact analysis
- [ ] Unit tests

---

## Phase 6: CLI Application (Week 11-12)

### 6.1 Create Ast.Cli Project

**Objective**: Command-line interface for all operations.

**Dependencies:**
```xml
<PackageReference Include="Spectre.Console" Version="0.53.0" />
<PackageReference Include="Spectre.Console.Cli" Version="0.53.0" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
```

**Commands:**
```
ast-cli index <repository-path>     # Parse repo, generate embeddings, store to DBs
ast-cli search <query>              # Natural language code search
ast-cli callers <method-name>       # Find all callers of a method
ast-cli callees <method-name>       # Find all methods called by a method
ast-cli impact <file:line>          # Analyze change impact
ast-cli similar <file:line>         # Find similar code
ast-cli export <output-path>        # Export hypergraph to JSON/HyperNetX format
ast-cli serve                       # Start HTTP API server
```

**Example Usage:**
```bash
# Index a repository
ast-cli index /path/to/repo --model qwen2.5-coder-0.5b --batch-size 32

# Search for authentication code
ast-cli search "validate user credentials" --top 10

# Find impact of changing a method
ast-cli impact "src/Auth/TokenValidator.cs:45"

# Export for visualization
ast-cli export ./output --format hypernetx
```

**Deliverables:**
- [ ] `Ast.Cli` project
- [ ] All commands implemented
- [ ] Progress reporting with Spectre.Console
- [ ] Configuration via appsettings.json and CLI args
- [ ] Error handling and logging

---

## Phase 7: Integration Testing (Week 13-14)

### 7.1 Create Ast.IntegrationTests Project

**Test Scenarios:**

```gherkin
Feature: End-to-End Code Reasoning

  @integration @slow
  Scenario: Index and search a repository
    Given a repository with C# and PowerShell files
    When I index the repository
    Then the graph database should contain nodes and edges
    And the vector database should contain embeddings
    When I search for "database connection"
    Then I should receive relevant code results

  @integration
  Scenario: Cross-language reference detection
    Given a PowerShell script that calls a C# type "MyApp.Services.DataProcessor"
    When I index the repository
    Then there should be a cross-language hyperedge linking PowerShell to C#

  @integration
  Scenario: Impact analysis accuracy
    Given an indexed repository with method "ProcessOrder"
    When I analyze impact of changing "ProcessOrder"
    Then I should see all direct callers
    And I should see affected tests
```

**Deliverables:**
- [ ] `Ast.IntegrationTests` project
- [ ] Docker Compose for test infrastructure
- [ ] End-to-end test scenarios
- [ ] Performance benchmarks

---

## NuGet Package Additions to Directory.Packages.props

```xml
<!-- Add to Directory.Packages.props -->
<ItemGroup>
  <!-- Embedding / Local LLM -->
  <PackageVersion Include="LLamaSharp" Version="0.18.0" />
  <PackageVersion Include="LLamaSharp.Backend.Cuda12" Version="0.18.0" />

  <!-- Vector Database -->
  <PackageVersion Include="Qdrant.Client" Version="1.12.0" />

  <!-- Graph Database -->
  <PackageVersion Include="Neo4j.Driver" Version="5.28.3" />

  <!-- CLI -->
  <PackageVersion Include="Spectre.Console.Cli" Version="0.53.0" />
</ItemGroup>
```

---

## Timeline Summary

| Phase | Duration | Deliverables |
|-------|----------|--------------|
| 1. Foundation & Testing | 2 weeks | Unit tests, configuration system |
| 2. Embedding Generation | 2 weeks | LLamaSharp integration, embedding service |
| 3. Vector Database | 2 weeks | Qdrant integration, semantic search |
| 4. Graph Database | 2 weeks | Neo4j integration, graph traversal |
| 5. Query Interface | 2 weeks | Hybrid query service, impact analysis |
| 6. CLI Application | 2 weeks | Command-line tool |
| 7. Integration Testing | 2 weeks | E2E tests, benchmarks |

**Total: 14 weeks**

---

## Success Criteria

1. **Accuracy**: >90% precision on semantic code search
2. **Performance**: <5 seconds to search 1M node repository
3. **Coverage**: All C# and PowerShell constructs correctly parsed
4. **Reliability**: All tests passing, <1% failure rate in production

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| LLamaSharp compatibility issues | Fall back to REST API with local Ollama |
| Neo4j performance at scale | Add caching layer, consider graph partitioning |
| Embedding quality for code | Fine-tune or use code-specific model |
| Memory usage with large repos | Stream processing, batch operations |

---

## References

- [HyperGraphReasoning](https://github.com/smartpcr/hypergraphreasoning) - Inspiration project
- [LLamaSharp](https://github.com/SciSharp/LLamaSharp) - .NET LLM binding
- [Qdrant](https://qdrant.tech/) - Vector database
- [Neo4j](https://neo4j.com/) - Graph database
- [Microsoft Semantic Kernel](https://learn.microsoft.com/en-us/semantic-kernel/) - AI orchestration

---

## Sources

- [LLamaSharp GitHub](https://github.com/SciSharp/LLamaSharp)
- [Milvus .NET Integration](https://devblogs.microsoft.com/dotnet/get-started-milvus-vector-db-dotnet/)
- [Qdrant Semantic Kernel Connector](https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/qdrant-connector)
- [Neo4j .NET Driver](https://github.com/neo4j/neo4j-dotnet-driver)
- [Top Vector Databases 2025](https://medium.com/@fendylike/top-5-open-source-vector-search-engines-a-comprehensive-comparison-guide-for-2025-e10110b47aa3)
