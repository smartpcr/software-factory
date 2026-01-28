# Embedding Model Approaches for Repo-Level Code Intelligence

> Last updated: 2026-01-23

A comprehensive analysis of approaches to generate embeddings for repository-level code intelligence, combining insights from LSP, SCIP, and modern embedding techniques. Includes complete C# implementation guide and practical enhancements for hypergraph-based systems.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [The Problem Space](#the-problem-space)
3. [Approach Comparison](#approach-comparison)
4. [Embedding Models for Code](#embedding-models-for-code)
5. [Chunking Strategies](#chunking-strategies)
6. [Enrichment with Semantic Data](#enrichment-with-semantic-data)
7. [Storage Solutions](#storage-solutions)
8. [Implementation Architectures](#implementation-architectures)
9. [Recommendations by Use Case](#recommendations-by-use-case)
10. [Implementation Guide](#implementation-guide) (C#)
11. [C# Detailed Implementation Guide](#c-implementation-guide)
12. [Practical Approach: Building on Existing Hypergraph Implementation](#practical-approach-building-on-existing-hypergraph-implementation)
13. [Summary: Decision Tree](#summary-decision-tree)
14. [Sources](#sources)

---

## Executive Summary

### Key Findings

| Approach | Accuracy | Complexity | Best For |
|----------|----------|------------|----------|
| **Pure Embeddings** | Medium | Low | Quick setup, small repos |
| **AST-Aware Embeddings** | Medium-High | Medium | Structured code search |
| **SCIP + Embeddings** | High | High | Production systems |
| **LSP + Embeddings (Hybrid)** | **Highest** | High | Maximum accuracy |

### Recommended Stack

```
┌─────────────────────────────────────────────────────────────────┐
│                    Recommended Architecture                     │
│                                                                 │
│  ┌─────────────┐   ┌─────────────┐   ┌─────────────────────┐    │
│  │  SCIP/LSP   │ + │  AST-Aware  │ + │  Voyage-Code-3 or   │    │
│  │  Indexer    │   │  Chunking   │   │  CodeXEmbed-7B      │    │
│  └─────────────┘   └─────────────┘   └─────────────────────┘    │
│         │                 │                    │                │
│         └─────────────────┴────────────────────┘                │
│                           │                                     │
│                           ▼                                     │
│                    ┌─────────────┐                              │
│                    │   Qdrant    │                              │
│                    │  (Hybrid)   │                              │
│                    └─────────────┘                              │
└─────────────────────────────────────────────────────────────────┘
```

---

## The Problem Space

### Why Code Intelligence is Hard

Code is fundamentally different from natural language:

| Aspect | Natural Language | Code |
|--------|------------------|------|
| **Structure** | Loose, contextual | Strict syntax, AST |
| **References** | Implicit | Explicit (imports, calls) |
| **Types** | N/A | Critical for correctness |
| **Scope** | Paragraph/document | Function/class/module/repo |
| **Relationships** | Semantic | Structural + semantic |

### What We Want to Capture

```
┌─────────────────────────────────────────────────────────────────┐
│                  Code Intelligence Dimensions                   │
│                                                                 │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐  │
│  │    Syntactic    │  │    Semantic     │  │   Relational    │  │
│  │                 │  │                 │  │                 │  │
│  │ • AST structure │  │ • Types         │  │ • Call graph    │  │
│  │ • Tokens        │  │ • Intent        │  │ • Import graph  │  │
│  │ • Patterns      │  │ • Docstrings    │  │ • Inheritance   │  │
│  │ • Keywords      │  │ • Variable names│  │ • Dependencies  │  │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘  │
│                                                                 │
│  Pure Embeddings:     ████░░░░░░  (captures ~40%)               │
│  AST + Embeddings:    ██████░░░░  (captures ~60%)               │
│  SCIP + Embeddings:   ████████░░  (captures ~80%)               │
│  LSP + Embeddings:    ██████████  (captures ~95%)               │
└─────────────────────────────────────────────────────────────────┘
```

---

## Approach Comparison

### Approach 1: Pure Text Embeddings

**How it works:**
```
Code Files → Text Chunks → Embedding Model → Vector DB
```

**Pros:**
- Simple to implement
- Language-agnostic
- Fast indexing

**Cons:**
- Loses structural information
- Can split functions mid-body
- No type awareness
- Poor for precise lookups

**Best for:** Prototypes, documentation search, small codebases

---

### Approach 2: AST-Aware Embeddings

**How it works:**
```
Code Files → AST Parser → Semantic Chunks → Embedding Model → Vector DB
                │
                └── Functions, classes, methods as units
```

**Pros:**
- Maintains code structure
- Chunks are syntactically valid
- Better retrieval accuracy

**Cons:**
- No type information
- No cross-file relationships
- Requires per-language parser

**Best for:** Medium codebases, when you need structure but not types

---

### Approach 3: SCIP/LSIF + Embeddings

**How it works:**
```
Code Files → SCIP Indexer → Symbol Graph → Enriched Chunks → Embedding → Vector DB
                │                │
                │                └── Types, references, definitions
                └── Compiler-level analysis
```

**Pros:**
- Compiler-accurate type information
- Cross-file reference tracking
- Batch processing (not interactive)
- Persistent index

**Cons:**
- Limited language support (no PowerShell, Lua, etc.)
- Requires build setup for some languages
- Index can become stale

**Best for:** Production systems with supported languages

---

### Approach 4: LSP + Embeddings (Hybrid)

**How it works:**
```
Code Files → LSP Server → Extract Semantic Data → Enriched Chunks → Embedding → Vector DB
                │
                ├── Hover info (types)
                ├── Definitions
                ├── References
                └── Diagnostics
```

**Pros:**
- Maximum semantic richness
- Real-time accuracy (if re-indexed)
- Wider language support than SCIP
- Can capture errors/warnings

**Cons:**
- LSP designed for interactive use
- Slower than SCIP for batch
- Servers can be unstable
- Memory intensive

**Best for:** Maximum accuracy, languages without SCIP support

---

### Comparison Matrix

| Feature | Pure Text | AST-Aware | SCIP | LSP Hybrid |
|---------|-----------|-----------|------|------------|
| **Setup complexity** | Low | Medium | High | High |
| **Indexing speed** | Fast | Fast | Medium | Slow |
| **Type information** | ❌ | ❌ | ✅ | ✅ |
| **Cross-file refs** | ❌ | ❌ | ✅ | ✅ |
| **Call hierarchy** | ❌ | ❌ | ✅ | ✅ |
| **Language support** | All | Most | Limited | Most |
| **Chunk quality** | Low | High | High | High |
| **Staleness risk** | Low | Low | Medium | Low |
| **PowerShell** | ✅ | ✅ | ❌ | ✅ (config) |
| **C#** | ✅ | ✅ | ✅ | ✅ |

---

## Embedding Models for Code

### Model Comparison (2025)

| Model | MRR Score | Dimensions | Cost | Open Source |
|-------|-----------|------------|------|-------------|
| **CodeXEmbed-7B** | 98.1% | 4096 | Self-host | ✅ |
| **Voyage-Code-3** | 97.3% | 1024 | $0.06/1M tokens | ❌ |
| **OpenAI text-embedding-3-large** | 94.2% | 3072 | $0.13/1M tokens | ❌ |
| **OpenAI text-embedding-3-small** | 92.1% | 1536 | $0.02/1M tokens | ❌ |
| **Jina-embeddings-v2-base-code** | 89.5% | 768 | Self-host | ✅ |
| **UniXcoder** | 85.3% | 768 | Self-host | ✅ |
| **CodeBERT** | 78.2% | 768 | Self-host | ✅ |
| **all-MiniLM-L6-v2** | 82.1% | 384 | Self-host | ✅ |

### Recommendations

**Best Performance (Commercial):**
- **Voyage-Code-3** — Best price/performance ratio
- $0.06 per million tokens
- 97.3% MRR, purpose-built for code

**Best Performance (Open Source):**
- **CodeXEmbed-7B** — State-of-the-art open model
- Requires GPU for reasonable speed
- 20+ points better than Voyage-Code on benchmarks

**Best Budget Option:**
- **OpenAI text-embedding-3-small** — 95% of top performance at 1/3 cost
- $0.02 per million tokens
- Good balance for most use cases

**Best for Self-Hosting:**
- **Jina-embeddings-v2-base-code** — Strong performance, reasonable size
- 768 dimensions, runs on CPU
- Specifically trained for code

### Hybrid Embedding Strategy

Use multiple models for different purposes:

```csharp
// Required NuGet packages:
// - System.Net.Http.Json

// Strategy: Dual embeddings for hybrid search
var codeEmbedding = await voyageCode3.EmbedAsync(codeChunk);      // Code semantics
var textEmbedding = await openAISmall.EmbedAsync(docstring + names); // Natural language

// Store both, search with weighted combination
var combinedScore = 0.7f * codeScore + 0.3f * textScore;
```

---

## Chunking Strategies

### The Problem with Naive Chunking

```csharp
// BAD: Fixed-size chunking breaks code
public decimal CalculateTotal(List<Item> items)
{
    decimal total = 0;
    foreach (var item in ite  // <-- Chunk boundary cuts here!
// Next chunk starts with broken code:
ms)
    {
        total += item.Price;
    }
    return total;
}
```

### AST-Aware Chunking

**Principle:** Chunk at semantic boundaries (functions, classes, methods)

```
┌─────────────────────────────────────────────────────────────────┐
│                    AST-Aware Chunking Pipeline                  │
│                                                                 │
│  Source Code                                                    │
│       │                                                         │
│       ▼                                                         │
│  ┌─────────────┐                                                │
│  │ AST Parser  │  (Tree-sitter, language-specific parsers)      │
│  │             │                                                │
│  └──────┬──────┘                                                │
│         │                                                       │
│         ▼                                                       │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                    AST Traversal                        │    │
│  │                                                         │    │
│  │  Extract:                                               │    │
│  │  • Function definitions    • Class definitions          │    │
│  │  • Method definitions      • Interface definitions      │    │
│  │  • Type definitions        • Import statements          │    │
│  │  • Module-level constants  • Decorators/attributes      │    │
│  └──────────────────────────┬──────────────────────────────┘    │
│                             │                                   │
│                             ▼                                   │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                    Chunk Assembly                       │    │
│  │                                                         │    │
│  │  For each semantic unit:                                │    │
│  │  1. Include imports needed by this unit                 │    │
│  │  2. Include parent class/module context                 │    │
│  │  3. Include docstrings and comments                     │    │
│  │  4. Include type annotations                            │    │
│  │  5. Respect max token limit (truncate if needed)        │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

### Chunk Types and Sizes

| Chunk Type | Typical Size | Include With Chunk |
|------------|--------------|-------------------|
| **Function** | 50-500 tokens | Imports, decorators, docstring |
| **Method** | 50-300 tokens | Class signature, imports |
| **Class** | 200-2000 tokens | Imports, base classes |
| **Module** | Summary only | List of exports |
| **Type/Interface** | 20-200 tokens | Related types |

### Hierarchical Chunking Strategy

For large codebases, use multi-level indexing:

```
Level 1: Repository Summary
    │
    ├── Level 2: Module/Package Summaries
    │       │
    │       ├── Level 3: Class Summaries
    │       │       │
    │       │       └── Level 4: Method/Function Details
    │       │
    │       └── Level 4: Standalone Functions
    │
    └── Level 2: Configuration Files
```

**Search strategy:**
1. First, search Level 2 to find relevant modules
2. Then, search Level 4 within those modules
3. Return most relevant chunks with context

### Tools for AST-Aware Chunking

| Tool | Languages | Notes |
|------|-----------|-------|
| [Tree-sitter](https://tree-sitter.github.io/) | 100+ languages | Fast, incremental parsing |
| [code-chunk](https://github.com/supermemoryai/code-chunk) | JS/TS, Python, more | Ready-to-use AST chunking |
| [ast-grep](https://ast-grep.github.io/) | Many | Structural search + extract |
| Language-specific (Roslyn, rust-analyzer) | Single language | Deepest understanding |

---

## Enrichment with Semantic Data

### What to Enrich

Transform raw code chunks into semantically rich documents:

```
┌─────────────────────────────────────────────────────────────────┐
│                       Enriched Chunk                            │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Metadata                                                │    │
│  │ • file_path: "src/auth/oauth.ts"                        │    │
│  │ • symbol_name: "authenticateUser"                       │    │
│  │ • symbol_type: "function"                               │    │
│  │ • language: "typescript"                                │    │
│  │ • start_line: 42, end_line: 87                          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Semantic Data (from LSP/SCIP)                           │    │
│  │ • signature: "(user: User, token: string) => Promise<AuthResult>" │
│  │ • return_type: "Promise<AuthResult>"                    │    │
│  │ • parameters: [{name: "user", type: "User"}, ...]       │    │
│  │ • calls: ["validateToken", "fetchUserProfile", ...]     │    │
│  │ • called_by: ["loginHandler", "refreshAuth", ...]       │    │
│  │ • imports: ["User from ./types", "AuthResult from ..."] │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Code Content                                            │    │
│  │ """                                                     │    │
│  │ Authenticates a user with OAuth token.                  │    │
│  │                                                         │    │
│  │ @param user - The user to authenticate                  │    │
│  │ @param token - OAuth access token                       │    │
│  │ @returns Promise resolving to auth result               │    │
│  │ """                                                     │    │
│  │ async function authenticateUser(                        │    │
│  │   user: User,                                           │    │
│  │   token: string                                         │    │
│  │ ): Promise<AuthResult> {                                │    │
│  │   const valid = await validateToken(token);             │    │
│  │   ...                                                   │    │
│  │ }                                                       │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Embedding Input (what gets embedded)                    │    │
│  │                                                         │    │
│  │ "authenticateUser: Authenticates a user with OAuth      │    │
│  │  token. Parameters: user (User), token (string).        │    │
│  │  Returns: Promise<AuthResult>. Calls: validateToken,    │    │
│  │  fetchUserProfile. async function authenticateUser..."  │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

### Enrichment Sources

| Source | Data Provided | Availability |
|--------|---------------|--------------|
| **AST Parser** | Structure, names, signatures | All languages |
| **Docstrings** | Intent, usage | If present |
| **LSP Hover** | Full type signatures | Requires LSP server |
| **LSP References** | Who calls this, what it calls | Requires LSP server |
| **SCIP Index** | All of above, pre-computed | Supported languages |
| **Git Blame** | Author, last modified | Git repos |
| **Test Files** | Usage examples | If naming convention |

### Embedding Input Construction

Combine multiple signals for richer embeddings:

```csharp
// Required NuGet packages:
// (none - uses standard .NET libraries)

public static string BuildEmbeddingInput(EnrichedChunk chunk)
{
    var parts = new List<string>();

    // 1. Symbol identification
    parts.Add($"{chunk.SymbolName}: {chunk.SymbolType}");

    // 2. Natural language description (most important for search)
    if (!string.IsNullOrEmpty(chunk.Docstring))
        parts.Add(chunk.Docstring);

    // 3. Type signature (critical for type-aware search)
    if (!string.IsNullOrEmpty(chunk.Signature))
        parts.Add($"Signature: {chunk.Signature}");

    // 4. Relationships (for architectural understanding)
    if (chunk.Calls?.Count > 0)
        parts.Add($"Calls: {string.Join(", ", chunk.Calls.Take(10))}");
    if (chunk.CalledBy?.Count > 0)
        parts.Add($"Called by: {string.Join(", ", chunk.CalledBy.Take(10))}");

    // 5. Actual code (for implementation details)
    var truncatedCode = chunk.Code.Length > 2000
        ? chunk.Code[..2000]
        : chunk.Code;
    parts.Add(truncatedCode);

    return string.Join("\n", parts);
}
```

---

## Storage Solutions

### Vector Database Comparison

| Database | Type | Best For | Code Search Features |
|----------|------|----------|---------------------|
| **Qdrant** | Open source / Cloud | Production, hybrid search | Payload filtering, sparse vectors |
| **Pinecone** | Managed cloud | Simplicity, scale | Metadata filtering, namespaces |
| **Milvus** | Open source / Cloud | Massive scale | GPU acceleration, partitioning |
| **Chroma** | Open source | Prototyping | Simple API, embedded mode |
| **pgvector** | PostgreSQL extension | Existing Postgres users | SQL integration, ACID |
| **LanceDB** | Open source | Local-first, embedded | Serverless, columnar |

### Recommended: Qdrant

**Why Qdrant for code search:**

1. **Hybrid search** — Combine dense + sparse vectors
2. **Payload filtering** — Filter by language, file path, symbol type
3. **Rust performance** — Fast indexing and querying
4. **Self-hostable** — Full control, no vendor lock-in
5. **Sparse vectors** — Good for keyword matching in code

```csharp
// Required NuGet packages:
// - Qdrant.Client (1.9.0+)
// - System.Text.Json

using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Text.Json;

var client = new QdrantClient("localhost", 6334);

// Create collection with hybrid search
await client.CreateCollectionAsync(
    collectionName: "code_index",
    vectorsConfig: new VectorParams
    {
        Size = 1024,
        Distance = Distance.Cosine
    });

// Insert enriched chunk
var point = new PointStruct
{
    Id = new PointId { Uuid = Guid.NewGuid().ToString() },
    Vectors = codeEmbedding,
    Payload =
    {
        ["file_path"] = chunk.FilePath,
        ["symbol_name"] = chunk.SymbolName,
        ["symbol_type"] = chunk.SymbolType,
        ["language"] = chunk.Language,
        ["signature"] = chunk.Signature ?? "",
        ["code"] = chunk.Code,
        ["calls"] = JsonSerializer.Serialize(chunk.Calls),
        ["called_by"] = JsonSerializer.Serialize(chunk.CalledBy)
    }
};

await client.UpsertAsync("code_index", new[] { point });
```

### Storage Schema Design

```
┌─────────────────────────────────────────────────────────────────┐
│                      Storage Architecture                       │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                    Vector Database                      │    │
│  │                      (Qdrant)                           │    │
│  │                                                         │    │
│  │  Collection: code_chunks                                │    │
│  │  ├── Vector: dense (1024d, code embedding)              │    │
│  │  ├── Vector: sparse (keyword/BM25)                      │    │
│  │  └── Payload: metadata, code, relationships             │    │
│  │                                                         │    │
│  │  Collection: file_summaries                             │    │
│  │  ├── Vector: dense (file-level embedding)               │    │
│  │  └── Payload: file_path, exports, imports               │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                   Relational Database                   │    │
│  │                   (SQLite / Postgres)                   │    │
│  │                                                         │    │
│  │  Table: symbols                                         │    │
│  │  ├── id, name, type, file_path, line, signature         │    │
│  │  └── Indexes on name, file_path                         │    │
│  │                                                         │    │
│  │  Table: references                                      │    │
│  │  ├── from_symbol_id, to_symbol_id, type                 │    │
│  │  └── For call graph queries                             │    │
│  │                                                         │    │
│  │  Table: diagnostics                                     │    │
│  │  ├── file_path, line, severity, message                 │    │
│  │  └── For error tracking                                 │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                     File Storage                        │    │
│  │                                                         │    │
│  │  .code-index/                                           │    │
│  │  ├── scip/index.scip          # SCIP index (if used)    │    │
│  │  ├── chunks/                  # Cached chunk data       │    │
│  │  └── metadata.json            # Index metadata          │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

---

## Implementation Architectures

### Architecture A: Simple (Embeddings Only)

**Best for:** Small repos, quick setup, prototyping

```
┌─────────────────────────────────────────────────────────────────┐
│                    Simple Architecture                          │
│                                                                 │
│  ┌──────────┐   ┌───────────┐   ┌──────────┐   ┌──────────┐     │
│  │  Source  │──►│Tree-sitter│──►│ Embedding│──►│  Chroma  │     │
│  │  Files   │   │  Chunker  │   │  Model   │   │    DB    │     │
│  └──────────┘   └───────────┘   └──────────┘   └──────────┘     │
│                                                                 │
│  Complexity: Low                                                │
│  Accuracy: Medium                                               │
│  Languages: All (with Tree-sitter grammar)                      │
└─────────────────────────────────────────────────────────────────┘
```

**Implementation:**
```csharp
// Required NuGet packages:
// - Microsoft.CodeAnalysis.CSharp (4.9.2+) for Roslyn parsing
// - Qdrant.Client (1.9.0+) or use ChromaDB HTTP client
// - System.Net.Http.Json

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 1. Parse and chunk (using Roslyn for C#)
var code = await File.ReadAllTextAsync(filePath);
var tree = CSharpSyntaxTree.ParseText(code);
var root = await tree.GetRootAsync();
var chunks = root.DescendantNodes()
    .OfType<MethodDeclarationSyntax>()
    .Select(m => new CodeChunk(
        Code: m.ToFullString(),
        Name: m.Identifier.Text,
        File: filePath))
    .ToList();

// 2. Embed (using HTTP API - Jina, Voyage, or OpenAI)
var embeddings = await embeddingProvider.EmbedBatchAsync(
    chunks.Select(c => c.Code).ToList());

// 3. Store (using Qdrant)
var points = chunks.Zip(embeddings, (chunk, embedding) => new PointStruct
{
    Id = new PointId { Uuid = Guid.NewGuid().ToString() },
    Vectors = embedding,
    Payload = { ["file"] = chunk.File, ["name"] = chunk.Name, ["code"] = chunk.Code }
}).ToList();

await qdrantClient.UpsertAsync("code", points);
```

---

### Architecture B: Production (SCIP + Embeddings)

**Best for:** Production systems, supported languages

```
┌─────────────────────────────────────────────────────────────────┐
│                   Production Architecture                       │
│                                                                 │
│                    ┌──────────────────┐                         │
│                    │   SCIP Indexer   │                         │
│                    │  (scip-dotnet,   │                         │
│                    │   scip-typescript)│                        │
│                    └────────┬─────────┘                         │
│                             │                                   │
│  ┌──────────┐               ▼                                   │
│  │  Source  │──────►┌──────────────────┐                        │
│  │  Files   │       │   SCIP Index     │                        │
│  └──────────┘       │  (index.scip)    │                        │
│                     └────────┬─────────┘                        │
│                              │                                  │
│           ┌──────────────────┼──────────────────┐               │
│           │                  │                  │               │
│           ▼                  ▼                  ▼               │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐         │
│  │   Extract    │   │   Extract    │   │   Extract    │         │
│  │   Symbols    │   │   Types      │   │  References  │         │
│  └──────┬───────┘   └──────┬───────┘   └──────┬───────┘         │
│         │                  │                  │                 │
│         └──────────────────┼──────────────────┘                 │
│                            │                                    │
│                            ▼                                    │
│                   ┌──────────────────┐                          │
│                   │  Enrich Chunks   │                          │
│                   │  (add type info, │                          │
│                   │   references)    │                          │
│                   └────────┬─────────┘                          │
│                            │                                    │
│              ┌─────────────┼─────────────┐                      │
│              ▼             ▼             ▼                      │
│     ┌──────────────┐ ┌──────────┐ ┌──────────────┐              │
│     │  Voyage-Code │ │  Sparse  │ │   SQLite     │              │
│     │  Embeddings  │ │  Vectors │ │  (relations) │              │
│     └──────┬───────┘ └────┬─────┘ └──────┬───────┘              │
│            │              │              │                      │
│            └──────────────┼──────────────┘                      │
│                           ▼                                     │
│                    ┌─────────────┐                              │
│                    │   Qdrant    │                              │
│                    │  (hybrid)   │                              │
│                    └─────────────┘                              │
│                                                                 │
│  Complexity: High                                               │
│  Accuracy: High                                                 │
│  Languages: SCIP-supported (TS, Python, Go, Rust, C#, Java)     │
└─────────────────────────────────────────────────────────────────┘
```

---

### Architecture C: Maximum Accuracy (LSP + Embeddings)

**Best for:** Maximum accuracy, languages without SCIP support (PowerShell)

```
┌─────────────────────────────────────────────────────────────────┐
│                  Maximum Accuracy Architecture                  │
│                                                                 │
│  ┌──────────┐                                                   │
│  │  Source  │                                                   │
│  │  Files   │                                                   │
│  └────┬─────┘                                                   │
│       │                                                         │
│       ▼                                                         │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                 LSP Server Manager                      │    │
│  │                                                         │    │
│  │  ┌─────────┐ ┌─────────┐ ┌─────────┐  ┌─────────┐       │    │
│  │  │TS Server│ │Pyright  │ │  PSES   │  │OmniSharp│       │    │
│  │  │         │ │         │ │(PowerShell)│         │       │    │
│  │  └────┬────┘ └────┬────┘ └────┬────┘  └────┬────┘       │    │
│  │       └───────────┴───────────┴────────────┘            │    │
│  │                         │                               │    │
│  └─────────────────────────┼───────────────────────────────┘    │
│                            │                                    │
│                            ▼                                    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                   LSP Query Engine                      │    │
│  │                                                         │    │
│  │  For each file:                                         │    │
│  │  1. textDocument/documentSymbol  → Get all symbols      │    │
│  │  2. textDocument/hover           → Get type info        │    │
│  │  3. textDocument/references      → Get usages           │    │
│  │  4. textDocument/definition      → Get implementations  │    │
│  │  5. callHierarchy/incomingCalls  → Get callers          │    │
│  │  6. callHierarchy/outgoingCalls  → Get callees          │    │
│  └─────────────────────────┬───────────────────────────────┘    │
│                            │                                    │
│                            ▼                                    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                 Semantic Data Store                     │    │
│  │                                                         │    │
│  │  • Symbols with full type signatures                    │    │
│  │  • Call graph (who calls what)                          │    │
│  │  • Reference graph (where symbols are used)             │    │
│  │  • Diagnostics (errors, warnings)                       │    │
│  └─────────────────────────┬───────────────────────────────┘    │
│                            │                                    │
│                            ▼                                    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │                 Enriched Chunk Builder                  │    │
│  │                                                         │    │
│  │  Combine: AST chunk + LSP types + references + docs     │    │
│  └─────────────────────────┬───────────────────────────────┘    │
│                            │                                    │
│                            ▼                                    │
│                    ┌──────────────┐                             │
│                    │  Embedding   │                             │
│                    │   + Store    │                             │
│                    └──────────────┘                             │
│                                                                 │
│  Complexity: Very High                                          │
│  Accuracy: Maximum                                              │
│  Languages: All with LSP support (including PowerShell)         │
└─────────────────────────────────────────────────────────────────┘
```

---

## Recommendations by Use Case

### Use Case 1: Small Repo / Quick Start

**Scenario:** <10K lines, want to try code search quickly

**Recommendation:**
```
Approach: AST-Aware Embeddings
Chunking: Tree-sitter + function-level
Embedding: OpenAI text-embedding-3-small ($0.02/1M)
Storage: Chroma (embedded)
```

**Effort:** 1-2 days

---

### Use Case 2: Medium TypeScript/Python Repo

**Scenario:** 10K-100K lines, production use, TypeScript or Python

**Recommendation:**
```
Approach: SCIP + Embeddings
Indexer: scip-typescript or scip-python
Chunking: SCIP symbols + enrichment
Embedding: Voyage-Code-3 ($0.06/1M)
Storage: Qdrant (self-hosted or cloud)
```

**Effort:** 1-2 weeks

---

### Use Case 3: Enterprise C#/.NET

**Scenario:** Large .NET codebase, need type-aware search

**Recommendation:**
```
Approach: SCIP + Embeddings
Indexer: scip-dotnet (Roslyn-based)
Chunking: SCIP symbols + solution structure
Embedding: Voyage-Code-3 or CodeXEmbed-7B
Storage: Qdrant + SQL Server (for relations)
```

**Effort:** 2-4 weeks

---

### Use Case 4: PowerShell/Mixed Language

**Scenario:** PowerShell scripts + other languages without SCIP

**Recommendation:**
```
Approach: LSP + Embeddings (Hybrid)
LSP Servers: PowerShellEditorServices + language-specific
Chunking: AST + LSP enrichment
Embedding: Voyage-Code-3
Storage: Qdrant

Note: Must implement batch LSP querying (not trivial)
```

**Effort:** 3-6 weeks

---

### Use Case 5: Maximum Accuracy / Research

**Scenario:** Need highest possible accuracy, any complexity acceptable

**Recommendation:**
```
Approach: Full Hybrid (SCIP where available + LSP fallback)
Indexers: SCIP for supported, LSP for others
Chunking: Multi-level (repo → module → class → function)
Embedding: CodeXEmbed-7B (self-hosted) + sparse vectors
Storage: Qdrant (hybrid) + PostgreSQL (graph queries)
Search: Multi-stage (file → function → line)
```

**Effort:** 2-3 months

---

## Implementation Guide

### Step-by-Step: SCIP + Embeddings for C#/.NET

**1. Generate SCIP Index**

```bash
# Install scip-dotnet (requires .NET SDK)
dotnet tool install -g scip-dotnet

# Generate index for a solution
scip-dotnet index --output index.scip MySolution.sln

# Or for a specific project
scip-dotnet index --output index.scip MyProject.csproj
```

**2. Parse SCIP Index**

```csharp
// Required NuGet packages:
// - Google.Protobuf (3.26.1+)
// Note: Generate C# classes from scip.proto using protoc

using Google.Protobuf;

// Load index
var bytes = await File.ReadAllBytesAsync("index.scip");
var index = Scip.Index.Parser.ParseFrom(bytes);

// Extract symbols
var symbols = new Dictionary<string, SymbolInfo>();
foreach (var doc in index.Documents)
{
    foreach (var occ in doc.Occurrences)
    {
        if (!string.IsNullOrEmpty(occ.Symbol))
        {
            symbols[occ.Symbol] = new SymbolInfo(
                File: doc.RelativePath,
                Range: occ.Range.ToArray(),
                Roles: occ.SymbolRoles);
        }
    }
}

public record SymbolInfo(string File, int[] Range, int Roles);
```

**3. Build Enriched Chunks**

```csharp
// Required NuGet packages:
// - Google.Protobuf (3.26.1+)

public List<EnrichedChunk> BuildChunks(
    Scip.Index index,
    Dictionary<string, string> sourceFiles)
{
    var chunks = new List<EnrichedChunk>();

    foreach (var doc in index.Documents)
    {
        var filePath = doc.RelativePath;
        if (!sourceFiles.TryGetValue(filePath, out var source))
            continue;

        // Group occurrences by symbol - find definitions
        var definitions = doc.Occurrences
            .Where(o => (o.SymbolRoles & (int)Scip.SymbolRole.Definition) != 0)
            .ToList();

        foreach (var defn in definitions)
        {
            // Extract code range
            var code = ExtractRange(source, defn.Range.ToArray());

            // Find references
            var refs = FindReferences(index, defn.Symbol);

            // Build chunk
            chunks.Add(new EnrichedChunk
            {
                Symbol = defn.Symbol,
                File = filePath,
                Code = code,
                References = refs,
                Signature = ExtractSignature(defn.Symbol)
            });
        }
    }

    return chunks;
}

public record EnrichedChunk
{
    public required string Symbol { get; init; }
    public required string File { get; init; }
    public required string Code { get; init; }
    public required List<string> References { get; init; }
    public string? Signature { get; init; }
}
```

**4. Generate Embeddings**

```csharp
// Required NuGet packages:
// - System.Net.Http.Json

using System.Net.Http.Json;

public class VoyageEmbeddingClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public VoyageEmbeddingClient(string apiKey)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.voyageai.com/v1/")
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<List<float[]>> EmbedChunksAsync(List<EnrichedChunk> chunks)
    {
        // Build embedding input
        var texts = chunks.Select(chunk =>
        {
            var text = $"{chunk.Symbol}\n";
            text += $"Signature: {chunk.Signature}\n";
            text += $"Used by: {string.Join(", ", chunk.References.Take(5))}\n";
            text += chunk.Code;
            return text;
        }).ToList();

        // Batch embed
        var request = new { input = texts, model = "voyage-code-3", input_type = "document" };
        var response = await _httpClient.PostAsJsonAsync("embeddings", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<VoyageResponse>();
        return result!.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
    }

    private record VoyageResponse(VoyageEmbedding[] Data);
    private record VoyageEmbedding(int Index, float[] Embedding);

    public void Dispose() => _httpClient.Dispose();
}
```

**5. Store in Qdrant**

```csharp
// Required NuGet packages:
// - Qdrant.Client (1.9.0+)

using Qdrant.Client;
using Qdrant.Client.Grpc;

var client = new QdrantClient("localhost", 6334);

// Create collection
await client.CreateCollectionAsync(
    collectionName: "code",
    vectorsConfig: new VectorParams { Size = 1024, Distance = Distance.Cosine });

// Insert
var points = chunks.Zip(embeddings, (chunk, embedding) => new PointStruct
{
    Id = new PointId { Uuid = Guid.NewGuid().ToString() },
    Vectors = embedding,
    Payload =
    {
        ["symbol"] = chunk.Symbol,
        ["file"] = chunk.File,
        ["code"] = chunk.Code,
        ["signature"] = chunk.Signature ?? ""
    }
}).ToList();

await client.UpsertAsync("code", points);
```

**6. Search**

```csharp
// Required NuGet packages:
// - Qdrant.Client (1.9.0+)

public async Task<List<SearchResult>> SearchCodeAsync(
    string query,
    int limit = 5,
    CancellationToken cancellationToken = default)
{
    // Embed query
    var queryEmbedding = await _voyageClient.EmbedAsync(query, inputType: "query");

    // Search
    var results = await _qdrantClient.SearchAsync(
        collectionName: "code",
        vector: queryEmbedding,
        limit: (ulong)limit,
        cancellationToken: cancellationToken);

    return results.Select(r => new SearchResult
    {
        Symbol = r.Payload["symbol"].StringValue,
        File = r.Payload["file"].StringValue,
        Score = r.Score,
        Code = r.Payload["code"].StringValue
    }).ToList();
}

public record SearchResult
{
    public required string Symbol { get; init; }
    public required string File { get; init; }
    public required float Score { get; init; }
    public required string Code { get; init; }
}

// Example usage:
var results = await SearchCodeAsync("authenticate user with OAuth");
foreach (var result in results)
{
    Console.WriteLine($"[{result.Score:F3}] {result.Symbol} in {result.File}");
}
```

---

## C# Detailed Implementation Guide

This section provides a complete C# implementation for building a code intelligence system with embeddings, with full project structure and comprehensive code examples.

### Project Structure

```
CodeIntelligence/
├── CodeIntelligence.sln
├── src/
│   ├── CodeIntelligence.Core/           # Core models and interfaces
│   │   ├── Models/
│   │   │   ├── CodeChunk.cs
│   │   │   ├── EnrichedChunk.cs
│   │   │   ├── SymbolInfo.cs
│   │   │   └── SearchResult.cs
│   │   ├── Interfaces/
│   │   │   ├── ICodeExtractor.cs
│   │   │   ├── IEmbeddingProvider.cs
│   │   │   ├── IVectorStore.cs
│   │   │   └── ISemanticEnricher.cs
│   │   └── CodeIntelligence.Core.csproj
│   │
│   ├── CodeIntelligence.Extraction/     # Code extraction (Roslyn, Tree-sitter)
│   │   ├── Roslyn/
│   │   │   ├── RoslynExtractor.cs
│   │   │   └── RoslynSymbolVisitor.cs
│   │   ├── TreeSitter/
│   │   │   └── TreeSitterExtractor.cs
│   │   ├── Scip/
│   │   │   └── ScipIndexParser.cs
│   │   └── CodeIntelligence.Extraction.csproj
│   │
│   ├── CodeIntelligence.Enrichment/     # LSP and semantic enrichment
│   │   ├── Lsp/
│   │   │   ├── LspClient.cs
│   │   │   └── LspEnricher.cs
│   │   └── CodeIntelligence.Enrichment.csproj
│   │
│   ├── CodeIntelligence.Embedding/      # Embedding providers
│   │   ├── Providers/
│   │   │   ├── VoyageEmbeddingProvider.cs
│   │   │   ├── OpenAIEmbeddingProvider.cs
│   │   │   └── LocalEmbeddingProvider.cs
│   │   └── CodeIntelligence.Embedding.csproj
│   │
│   ├── CodeIntelligence.Storage/        # Vector storage
│   │   ├── Qdrant/
│   │   │   └── QdrantVectorStore.cs
│   │   ├── Sqlite/
│   │   │   └── SqliteMetadataStore.cs
│   │   └── CodeIntelligence.Storage.csproj
│   │
│   └── CodeIntelligence.Pipeline/       # Orchestration
│       ├── IndexingPipeline.cs
│       ├── SearchPipeline.cs
│       └── CodeIntelligence.Pipeline.csproj
│
├── tests/
│   └── CodeIntelligence.Tests/
│
└── samples/
    └── CodeIntelligence.Sample/
```

### NuGet Packages

```xml
<!-- CodeIntelligence.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>

<!-- CodeIntelligence.Extraction.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Roslyn for C# analysis -->
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.9.2" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.9.2" />
    <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />

    <!-- Tree-sitter for multi-language -->
    <PackageReference Include="TreeSitter.Bindings" Version="1.0.0" />

    <!-- Protobuf for SCIP -->
    <PackageReference Include="Google.Protobuf" Version="3.26.1" />
  </ItemGroup>
</Project>

<!-- CodeIntelligence.Enrichment.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- LSP client -->
    <PackageReference Include="OmniSharp.Extensions.LanguageClient" Version="0.19.9" />
    <PackageReference Include="StreamJsonRpc" Version="2.17.8" />
  </ItemGroup>
</Project>

<!-- CodeIntelligence.Embedding.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.SemanticKernel" Version="1.6.3" />
    <PackageReference Include="Azure.AI.OpenAI" Version="1.0.0-beta.17" />
  </ItemGroup>
</Project>

<!-- CodeIntelligence.Storage.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Qdrant client -->
    <PackageReference Include="Qdrant.Client" Version="1.9.0" />

    <!-- SQLite for metadata -->
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.3" />
    <PackageReference Include="Dapper" Version="2.1.35" />
  </ItemGroup>
</Project>
```

### Step 1: Core Models

```csharp
// Models/CodeChunk.cs
namespace CodeIntelligence.Core.Models;

public record CodeChunk
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required string Language { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required SymbolKind Kind { get; init; }
    public required string SymbolName { get; init; }
    public string? Namespace { get; init; }
    public string? ParentSymbol { get; init; }
}

public enum SymbolKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Method,
    Property,
    Field,
    Constructor,
    Function,
    Module,
    Constant
}

// Models/EnrichedChunk.cs
namespace CodeIntelligence.Core.Models;

public record EnrichedChunk : CodeChunk
{
    public string? Signature { get; init; }
    public string? ReturnType { get; init; }
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = [];
    public IReadOnlyList<string> Calls { get; init; } = [];
    public IReadOnlyList<string> CalledBy { get; init; } = [];
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<string> Imports { get; init; } = [];
    public string? Documentation { get; init; }
    public IReadOnlyList<DiagnosticInfo> Diagnostics { get; init; } = [];

    public string BuildEmbeddingInput()
    {
        var parts = new List<string>
        {
            $"{SymbolName}: {Kind}"
        };

        if (!string.IsNullOrEmpty(Documentation))
            parts.Add(Documentation);

        if (!string.IsNullOrEmpty(Signature))
            parts.Add($"Signature: {Signature}");

        if (Parameters.Count > 0)
            parts.Add($"Parameters: {string.Join(", ", Parameters.Select(p => $"{p.Name}: {p.Type}"))}");

        if (!string.IsNullOrEmpty(ReturnType))
            parts.Add($"Returns: {ReturnType}");

        if (Calls.Count > 0)
            parts.Add($"Calls: {string.Join(", ", Calls.Take(10))}");

        if (CalledBy.Count > 0)
            parts.Add($"Called by: {string.Join(", ", CalledBy.Take(10))}");

        parts.Add(Content);

        return string.Join("\n", parts);
    }
}

public record ParameterInfo(string Name, string Type);
public record DiagnosticInfo(string Message, int Line, DiagnosticSeverity Severity);
public enum DiagnosticSeverity { Error, Warning, Info, Hint }

// Models/SearchResult.cs
namespace CodeIntelligence.Core.Models;

public record SearchResult
{
    public required EnrichedChunk Chunk { get; init; }
    public required float Score { get; init; }
    public IReadOnlyList<string> MatchedTerms { get; init; } = [];
}
```

### Step 2: Interfaces

```csharp
// Interfaces/ICodeExtractor.cs
namespace CodeIntelligence.Core.Interfaces;

public interface ICodeExtractor
{
    IAsyncEnumerable<CodeChunk> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<CodeChunk> ExtractFromDirectoryAsync(
        string directoryPath,
        string[] filePatterns,
        CancellationToken cancellationToken = default);

    bool SupportsLanguage(string language);
}

// Interfaces/ISemanticEnricher.cs
namespace CodeIntelligence.Core.Interfaces;

public interface ISemanticEnricher
{
    Task<EnrichedChunk> EnrichAsync(
        CodeChunk chunk,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnrichedChunk>> EnrichBatchAsync(
        IReadOnlyList<CodeChunk> chunks,
        CancellationToken cancellationToken = default);
}

// Interfaces/IEmbeddingProvider.cs
namespace CodeIntelligence.Core.Interfaces;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);

    int Dimensions { get; }
    string ModelName { get; }
}

// Interfaces/IVectorStore.cs
namespace CodeIntelligence.Core.Interfaces;

public interface IVectorStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(
        EnrichedChunk chunk,
        float[] embedding,
        CancellationToken cancellationToken = default);

    Task UpsertBatchAsync(
        IReadOnlyList<(EnrichedChunk Chunk, float[] Embedding)> items,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryEmbedding,
        int limit = 10,
        SearchFilter? filter = null,
        CancellationToken cancellationToken = default);

    Task DeleteByFileAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

public record SearchFilter
{
    public string? Language { get; init; }
    public string? FilePathPrefix { get; init; }
    public SymbolKind? Kind { get; init; }
}
```

### Step 3: Roslyn-Based C# Extraction

```csharp
// Roslyn/RoslynExtractor.cs
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Core.Models;

namespace CodeIntelligence.Extraction.Roslyn;

public class RoslynExtractor : ICodeExtractor
{
    private static bool _msBuildRegistered;
    private static readonly object _lock = new();

    public RoslynExtractor()
    {
        EnsureMSBuildRegistered();
    }

    private static void EnsureMSBuildRegistered()
    {
        lock (_lock)
        {
            if (_msBuildRegistered) return;
            MSBuildLocator.RegisterDefaults();
            _msBuildRegistered = true;
        }
    }

    public bool SupportsLanguage(string language)
        => language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
           language.Equals("cs", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<CodeChunk> ExtractAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var code = await File.ReadAllTextAsync(filePath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(code, path: filePath);
        var root = await tree.GetRootAsync(cancellationToken);

        var visitor = new RoslynSymbolVisitor(filePath, code);
        visitor.Visit(root);

        foreach (var chunk in visitor.Chunks)
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<CodeChunk> ExtractFromDirectoryAsync(
        string directoryPath,
        string[] filePatterns,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Find solution or project files
        var solutionFiles = Directory.GetFiles(directoryPath, "*.sln", SearchOption.AllDirectories);

        if (solutionFiles.Length > 0)
        {
            // Use MSBuild workspace for full semantic analysis
            await foreach (var chunk in ExtractFromSolutionAsync(
                solutionFiles[0], cancellationToken))
            {
                yield return chunk;
            }
        }
        else
        {
            // Fall back to file-by-file parsing
            foreach (var pattern in filePatterns)
            {
                var files = Directory.GetFiles(directoryPath, pattern, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await foreach (var chunk in ExtractAsync(file, cancellationToken))
                    {
                        yield return chunk;
                    }
                }
            }
        }
    }

    private async IAsyncEnumerable<CodeChunk> ExtractFromSolutionAsync(
        string solutionPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                var code = root.ToFullString();

                var visitor = new RoslynSemanticVisitor(
                    syntaxTree.FilePath,
                    code,
                    semanticModel);
                visitor.Visit(root);

                foreach (var chunk in visitor.Chunks)
                {
                    yield return chunk;
                }
            }
        }
    }
}

// Roslyn/RoslynSymbolVisitor.cs
namespace CodeIntelligence.Extraction.Roslyn;

public class RoslynSymbolVisitor : CSharpSyntaxWalker
{
    private readonly string _filePath;
    private readonly string _code;
    private readonly List<CodeChunk> _chunks = [];

    public IReadOnlyList<CodeChunk> Chunks => _chunks;

    public RoslynSymbolVisitor(string filePath, string code)
    {
        _filePath = filePath;
        _code = code;
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        AddChunk(node, SymbolKind.Class, node.Identifier.Text, GetNamespace(node));
        base.VisitClassDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        AddChunk(node, SymbolKind.Interface, node.Identifier.Text, GetNamespace(node));
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        AddChunk(node, SymbolKind.Struct, node.Identifier.Text, GetNamespace(node));
        base.VisitStructDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        AddChunk(node, SymbolKind.Enum, node.Identifier.Text, GetNamespace(node));
        // Don't visit children for enums
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var parent = GetParentTypeName(node);
        AddChunk(node, SymbolKind.Method, node.Identifier.Text, GetNamespace(node), parent);
        // Don't visit children - method is atomic unit
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var parent = GetParentTypeName(node);
        AddChunk(node, SymbolKind.Constructor, node.Identifier.Text, GetNamespace(node), parent);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        // Only include properties with complex logic (not auto-properties)
        if (node.AccessorList?.Accessors.Any(a => a.Body != null || a.ExpressionBody != null) == true)
        {
            var parent = GetParentTypeName(node);
            AddChunk(node, SymbolKind.Property, node.Identifier.Text, GetNamespace(node), parent);
        }
    }

    private void AddChunk(
        SyntaxNode node,
        SymbolKind kind,
        string name,
        string? ns,
        string? parent = null)
    {
        var span = node.GetLocation().GetLineSpan();
        var content = node.ToFullString();

        // Include leading trivia (comments, attributes)
        if (node.HasLeadingTrivia)
        {
            var leadingTrivia = node.GetLeadingTrivia()
                .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                           t.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                           t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                           t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                .Select(t => t.ToFullString());

            if (leadingTrivia.Any())
            {
                content = string.Join("", leadingTrivia) + content;
            }
        }

        _chunks.Add(new CodeChunk
        {
            Id = $"{_filePath}:{span.StartLinePosition.Line}:{name}",
            FilePath = _filePath,
            Content = content,
            Language = "csharp",
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Kind = kind,
            SymbolName = name,
            Namespace = ns,
            ParentSymbol = parent
        });
    }

    private static string? GetNamespace(SyntaxNode node)
    {
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return ns?.Name.ToString();
    }

    private static string? GetParentTypeName(SyntaxNode node)
    {
        var parent = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return parent?.Identifier.Text;
    }
}

// Roslyn/RoslynSemanticVisitor.cs - With full semantic analysis
namespace CodeIntelligence.Extraction.Roslyn;

public class RoslynSemanticVisitor : CSharpSyntaxWalker
{
    private readonly string _filePath;
    private readonly string _code;
    private readonly SemanticModel _semanticModel;
    private readonly List<CodeChunk> _chunks = [];

    public IReadOnlyList<CodeChunk> Chunks => _chunks;

    public RoslynSemanticVisitor(string filePath, string code, SemanticModel semanticModel)
    {
        _filePath = filePath;
        _code = code;
        _semanticModel = semanticModel;
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol is null)
        {
            base.VisitMethodDeclaration(node);
            return;
        }

        var span = node.GetLocation().GetLineSpan();
        var documentation = symbol.GetDocumentationCommentXml();

        // Extract calls made by this method
        var calls = new List<string>();
        var invocations = node.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var invokedSymbol = _semanticModel.GetSymbolInfo(invocation).Symbol;
            if (invokedSymbol is IMethodSymbol method)
            {
                calls.Add($"{method.ContainingType?.Name}.{method.Name}");
            }
        }

        _chunks.Add(new CodeChunk
        {
            Id = $"{_filePath}:{span.StartLinePosition.Line}:{symbol.Name}",
            FilePath = _filePath,
            Content = node.ToFullString(),
            Language = "csharp",
            StartLine = span.StartLinePosition.Line + 1,
            EndLine = span.EndLinePosition.Line + 1,
            Kind = SymbolKind.Method,
            SymbolName = symbol.Name,
            Namespace = symbol.ContainingNamespace?.ToString(),
            ParentSymbol = symbol.ContainingType?.Name
        });
    }

    // Similar implementations for other syntax types...
}
```

### Step 4: LSP-Based Semantic Enrichment

```csharp
// Lsp/LspClient.cs
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using System.Diagnostics;

namespace CodeIntelligence.Enrichment.Lsp;

public class LspClient : IAsyncDisposable
{
    private readonly string _serverCommand;
    private readonly string[] _serverArgs;
    private readonly string _rootPath;

    private Process? _serverProcess;
    private ILanguageClient? _client;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public LspClient(string serverCommand, string[] serverArgs, string rootPath)
    {
        _serverCommand = serverCommand;
        _serverArgs = serverArgs;
        _rootPath = rootPath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            // Start LSP server process
            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _serverCommand,
                    Arguments = string.Join(" ", _serverArgs),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = _rootPath
                }
            };
            _serverProcess.Start();

            // Create LSP client
            _client = await LanguageClient.From(options =>
            {
                options
                    .WithInput(_serverProcess.StandardOutput.BaseStream)
                    .WithOutput(_serverProcess.StandardInput.BaseStream)
                    .WithRootPath(_rootPath)
                    .WithCapability(new TextDocumentClientCapabilities
                    {
                        Hover = new HoverCapability { DynamicRegistration = true },
                        Definition = new DefinitionCapability { DynamicRegistration = true },
                        References = new ReferenceCapability { DynamicRegistration = true },
                        DocumentSymbol = new DocumentSymbolCapability { DynamicRegistration = true }
                    });
            }, cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<Hover?> GetHoverAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        await OpenDocumentAsync(filePath, cancellationToken);

        return await _client!.TextDocument.RequestHover(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(line - 1, character)
        }, cancellationToken);
    }

    public async Task<LocationOrLocationLinks?> GetDefinitionAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        await OpenDocumentAsync(filePath, cancellationToken);

        return await _client!.TextDocument.RequestDefinition(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(line - 1, character)
        }, cancellationToken);
    }

    public async Task<IEnumerable<Location>> GetReferencesAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        await OpenDocumentAsync(filePath, cancellationToken);

        var result = await _client!.TextDocument.RequestReferences(new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(line - 1, character),
            Context = new ReferenceContext { IncludeDeclaration = false }
        }, cancellationToken);

        return result ?? [];
    }

    public async Task<SymbolInformationOrDocumentSymbolContainer?> GetDocumentSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        await OpenDocumentAsync(filePath, cancellationToken);

        return await _client!.TextDocument.RequestDocumentSymbol(new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        }, cancellationToken);
    }

    private readonly HashSet<string> _openDocuments = [];

    private async Task OpenDocumentAsync(string filePath, CancellationToken cancellationToken)
    {
        if (_openDocuments.Contains(filePath)) return;

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var uri = DocumentUri.FromFileSystemPath(filePath);

        _client!.TextDocument.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = GetLanguageId(filePath),
                Version = 1,
                Text = content
            }
        });

        _openDocuments.Add(filePath);

        // Wait for server to process
        await Task.Delay(100, cancellationToken);
    }

    private static string GetLanguageId(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".rs" => "rust",
            ".go" => "go",
            ".ps1" or ".psm1" or ".psd1" => "powershell",
            _ => "plaintext"
        };

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }

        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            _serverProcess.Kill();
            await _serverProcess.WaitForExitAsync();
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }
}

// Lsp/LspEnricher.cs
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Core.Models;

namespace CodeIntelligence.Enrichment.Lsp;

public class LspEnricher : ISemanticEnricher, IAsyncDisposable
{
    private readonly Dictionary<string, LspClient> _clients = [];
    private readonly LspServerConfig[] _serverConfigs;

    public LspEnricher(LspServerConfig[] serverConfigs)
    {
        _serverConfigs = serverConfigs;
    }

    public async Task<EnrichedChunk> EnrichAsync(
        CodeChunk chunk,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateClientAsync(chunk.Language, chunk.FilePath, cancellationToken);
        if (client is null)
        {
            return ToEnrichedChunk(chunk);
        }

        // Get hover info for the symbol
        var hover = await client.GetHoverAsync(
            chunk.FilePath,
            chunk.StartLine,
            0, // Start of line
            cancellationToken);

        var signature = ExtractSignatureFromHover(hover);
        var documentation = ExtractDocumentationFromHover(hover);

        // Get references
        var references = await client.GetReferencesAsync(
            chunk.FilePath,
            chunk.StartLine,
            0,
            cancellationToken);

        var refList = references
            .Select(r => $"{Path.GetFileName(r.Uri.Path)}:{r.Range.Start.Line + 1}")
            .ToList();

        return new EnrichedChunk
        {
            Id = chunk.Id,
            FilePath = chunk.FilePath,
            Content = chunk.Content,
            Language = chunk.Language,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            Kind = chunk.Kind,
            SymbolName = chunk.SymbolName,
            Namespace = chunk.Namespace,
            ParentSymbol = chunk.ParentSymbol,
            Signature = signature,
            Documentation = documentation,
            References = refList
        };
    }

    public async Task<IReadOnlyList<EnrichedChunk>> EnrichBatchAsync(
        IReadOnlyList<CodeChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var results = new List<EnrichedChunk>();

        // Group by file to minimize document opens
        var byFile = chunks.GroupBy(c => c.FilePath);

        foreach (var group in byFile)
        {
            foreach (var chunk in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var enriched = await EnrichAsync(chunk, cancellationToken);
                results.Add(enriched);
            }
        }

        return results;
    }

    private async Task<LspClient?> GetOrCreateClientAsync(
        string language,
        string filePath,
        CancellationToken cancellationToken)
    {
        var config = _serverConfigs.FirstOrDefault(c =>
            c.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));

        if (config is null) return null;

        var rootPath = FindProjectRoot(filePath, config.RootMarkers);
        var key = $"{config.Name}:{rootPath}";

        if (!_clients.TryGetValue(key, out var client))
        {
            client = new LspClient(config.Command, config.Args, rootPath);
            await client.InitializeAsync(cancellationToken);
            _clients[key] = client;
        }

        return client;
    }

    private static string FindProjectRoot(string filePath, string[] markers)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (markers.Any(m => File.Exists(Path.Combine(dir, m)) ||
                                 Directory.Exists(Path.Combine(dir, m))))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetDirectoryName(filePath)!;
    }

    private static EnrichedChunk ToEnrichedChunk(CodeChunk chunk) =>
        new()
        {
            Id = chunk.Id,
            FilePath = chunk.FilePath,
            Content = chunk.Content,
            Language = chunk.Language,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            Kind = chunk.Kind,
            SymbolName = chunk.SymbolName,
            Namespace = chunk.Namespace,
            ParentSymbol = chunk.ParentSymbol
        };

    private static string? ExtractSignatureFromHover(Hover? hover)
    {
        if (hover?.Contents.MarkedStrings is { } markedStrings)
        {
            var codeBlock = markedStrings.FirstOrDefault(m =>
                m.Language is not null || m.Value.StartsWith("```"));
            return codeBlock?.Value?.Trim('`', '\n', ' ');
        }
        return hover?.Contents.MarkupContent?.Value?.Split('\n').FirstOrDefault();
    }

    private static string? ExtractDocumentationFromHover(Hover? hover)
    {
        if (hover?.Contents.MarkupContent?.Value is { } value)
        {
            var lines = value.Split('\n');
            var docLines = lines.SkipWhile(l => l.StartsWith("```")).ToList();
            return docLines.Count > 0 ? string.Join("\n", docLines) : null;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
        _clients.Clear();
    }
}

public record LspServerConfig
{
    public required string Name { get; init; }
    public required string[] Languages { get; init; }
    public required string Command { get; init; }
    public required string[] Args { get; init; }
    public required string[] RootMarkers { get; init; }
}
```

### Step 5: Embedding Providers

```csharp
// Providers/VoyageEmbeddingProvider.cs
using System.Net.Http.Json;
using System.Text.Json;
using CodeIntelligence.Core.Interfaces;

namespace CodeIntelligence.Embedding.Providers;

public class VoyageEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public int Dimensions => 1024;
    public string ModelName => _model;

    public VoyageEmbeddingProvider(string apiKey, string model = "voyage-code-3")
    {
        _model = model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.voyageai.com/v1/")
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await EmbedBatchAsync([text], cancellationToken);
        return result[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            input = texts,
            model = _model,
            input_type = "document"
        };

        var response = await _httpClient.PostAsJsonAsync(
            "embeddings",
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<VoyageResponse>(
            cancellationToken: cancellationToken);

        return result!.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    private record VoyageResponse(VoyageEmbedding[] Data);
    private record VoyageEmbedding(int Index, float[] Embedding);

    public void Dispose() => _httpClient.Dispose();
}

// Providers/OpenAIEmbeddingProvider.cs
using Azure.AI.OpenAI;
using CodeIntelligence.Core.Interfaces;

namespace CodeIntelligence.Embedding.Providers;

public class OpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly OpenAIClient _client;
    private readonly string _model;
    private readonly int _dimensions;

    public int Dimensions => _dimensions;
    public string ModelName => _model;

    public OpenAIEmbeddingProvider(
        string apiKey,
        string model = "text-embedding-3-small",
        int dimensions = 1536)
    {
        _client = new OpenAIClient(apiKey);
        _model = model;
        _dimensions = dimensions;
    }

    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await EmbedBatchAsync([text], cancellationToken);
        return result[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var options = new EmbeddingsOptions(_model, texts)
        {
            Dimensions = _dimensions
        };

        var response = await _client.GetEmbeddingsAsync(options, cancellationToken);

        return response.Value.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding.ToArray())
            .ToList();
    }
}

// Providers/LocalEmbeddingProvider.cs - Using ONNX Runtime
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using CodeIntelligence.Core.Interfaces;

namespace CodeIntelligence.Embedding.Providers;

public class LocalEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _maxLength;

    public int Dimensions { get; }
    public string ModelName { get; }

    public LocalEmbeddingProvider(
        string modelPath,
        int dimensions = 768,
        int maxLength = 512)
    {
        _session = new InferenceSession(modelPath);
        Dimensions = dimensions;
        _maxLength = maxLength;
        ModelName = Path.GetFileNameWithoutExtension(modelPath);
    }

    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            // Tokenize (simplified - use proper tokenizer in production)
            var tokens = Tokenize(text);

            // Create input tensors
            var inputIds = new DenseTensor<long>(new[] { 1, tokens.Length });
            var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Length });

            for (int i = 0; i < tokens.Length; i++)
            {
                inputIds[0, i] = tokens[i];
                attentionMask[0, i] = 1;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
            };

            // Run inference
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            // Mean pooling
            var embedding = new float[Dimensions];
            for (int i = 0; i < Dimensions; i++)
            {
                float sum = 0;
                for (int j = 0; j < tokens.Length; j++)
                {
                    sum += output[0, j, i];
                }
                embedding[i] = sum / tokens.Length;
            }

            // Normalize
            var norm = MathF.Sqrt(embedding.Sum(x => x * x));
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= norm;
            }

            return embedding;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>();
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EmbedAsync(text, cancellationToken));
        }
        return results;
    }

    private long[] Tokenize(string text)
    {
        // Simplified tokenization - use HuggingFace tokenizers in production
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new long[Math.Min(words.Length + 2, _maxLength)];
        tokens[0] = 101; // [CLS]
        for (int i = 0; i < tokens.Length - 2 && i < words.Length; i++)
        {
            tokens[i + 1] = words[i].GetHashCode() % 30000 + 1000;
        }
        tokens[^1] = 102; // [SEP]
        return tokens;
    }

    public void Dispose() => _session.Dispose();
}
```

### Step 6: Qdrant Vector Storage

```csharp
// Qdrant/QdrantVectorStore.cs
using Qdrant.Client;
using Qdrant.Client.Grpc;
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Core.Models;
using System.Text.Json;

namespace CodeIntelligence.Storage.Qdrant;

public class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly int _dimensions;

    public QdrantVectorStore(
        string host = "localhost",
        int port = 6334,
        string collectionName = "code_index",
        int dimensions = 1024)
    {
        _client = new QdrantClient(host, port);
        _collectionName = collectionName;
        _dimensions = dimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var collections = await _client.ListCollectionsAsync(cancellationToken);

        if (!collections.Any(c => c == _collectionName))
        {
            await _client.CreateCollectionAsync(
                _collectionName,
                new VectorParams
                {
                    Size = (ulong)_dimensions,
                    Distance = Distance.Cosine
                },
                cancellationToken: cancellationToken);

            // Create payload indexes for filtering
            await _client.CreatePayloadIndexAsync(
                _collectionName,
                "file_path",
                PayloadSchemaType.Keyword,
                cancellationToken: cancellationToken);

            await _client.CreatePayloadIndexAsync(
                _collectionName,
                "language",
                PayloadSchemaType.Keyword,
                cancellationToken: cancellationToken);

            await _client.CreatePayloadIndexAsync(
                _collectionName,
                "kind",
                PayloadSchemaType.Keyword,
                cancellationToken: cancellationToken);
        }
    }

    public async Task UpsertAsync(
        EnrichedChunk chunk,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        await UpsertBatchAsync([(chunk, embedding)], cancellationToken);
    }

    public async Task UpsertBatchAsync(
        IReadOnlyList<(EnrichedChunk Chunk, float[] Embedding)> items,
        CancellationToken cancellationToken = default)
    {
        var points = items.Select((item, index) => new PointStruct
        {
            Id = new PointId { Uuid = CreateDeterministicGuid(item.Chunk.Id).ToString() },
            Vectors = item.Embedding,
            Payload =
            {
                ["id"] = item.Chunk.Id,
                ["file_path"] = item.Chunk.FilePath,
                ["symbol_name"] = item.Chunk.SymbolName,
                ["kind"] = item.Chunk.Kind.ToString(),
                ["language"] = item.Chunk.Language,
                ["start_line"] = item.Chunk.StartLine,
                ["end_line"] = item.Chunk.EndLine,
                ["namespace"] = item.Chunk.Namespace ?? "",
                ["parent_symbol"] = item.Chunk.ParentSymbol ?? "",
                ["signature"] = item.Chunk.Signature ?? "",
                ["return_type"] = item.Chunk.ReturnType ?? "",
                ["documentation"] = item.Chunk.Documentation ?? "",
                ["content"] = item.Chunk.Content,
                ["calls"] = JsonSerializer.Serialize(item.Chunk.Calls),
                ["called_by"] = JsonSerializer.Serialize(item.Chunk.CalledBy),
                ["references"] = JsonSerializer.Serialize(item.Chunk.References)
            }
        }).ToList();

        await _client.UpsertAsync(_collectionName, points, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryEmbedding,
        int limit = 10,
        SearchFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var qdrantFilter = BuildFilter(filter);

        var results = await _client.SearchAsync(
            _collectionName,
            queryEmbedding,
            limit: (ulong)limit,
            filter: qdrantFilter,
            cancellationToken: cancellationToken);

        return results.Select(r => new SearchResult
        {
            Score = r.Score,
            Chunk = PayloadToChunk(r.Payload)
        }).ToList();
    }

    public async Task DeleteByFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteAsync(
            _collectionName,
            new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "file_path",
                            Match = new Match { Keyword = filePath }
                        }
                    }
                }
            },
            cancellationToken: cancellationToken);
    }

    private static Filter? BuildFilter(SearchFilter? filter)
    {
        if (filter is null) return null;

        var conditions = new List<Condition>();

        if (filter.Language is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "language",
                    Match = new Match { Keyword = filter.Language }
                }
            });
        }

        if (filter.FilePathPrefix is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "file_path",
                    Match = new Match { Text = filter.FilePathPrefix }
                }
            });
        }

        if (filter.Kind is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = "kind",
                    Match = new Match { Keyword = filter.Kind.ToString() }
                }
            });
        }

        if (conditions.Count == 0) return null;

        var result = new Filter();
        result.Must.AddRange(conditions);
        return result;
    }

    private static EnrichedChunk PayloadToChunk(
        IDictionary<string, global::Qdrant.Client.Grpc.Value> payload)
    {
        return new EnrichedChunk
        {
            Id = payload["id"].StringValue,
            FilePath = payload["file_path"].StringValue,
            SymbolName = payload["symbol_name"].StringValue,
            Kind = Enum.Parse<SymbolKind>(payload["kind"].StringValue),
            Language = payload["language"].StringValue,
            StartLine = (int)payload["start_line"].IntegerValue,
            EndLine = (int)payload["end_line"].IntegerValue,
            Namespace = NullIfEmpty(payload["namespace"].StringValue),
            ParentSymbol = NullIfEmpty(payload["parent_symbol"].StringValue),
            Signature = NullIfEmpty(payload["signature"].StringValue),
            ReturnType = NullIfEmpty(payload["return_type"].StringValue),
            Documentation = NullIfEmpty(payload["documentation"].StringValue),
            Content = payload["content"].StringValue,
            Calls = JsonSerializer.Deserialize<List<string>>(
                payload["calls"].StringValue) ?? [],
            CalledBy = JsonSerializer.Deserialize<List<string>>(
                payload["called_by"].StringValue) ?? [],
            References = JsonSerializer.Deserialize<List<string>>(
                payload["references"].StringValue) ?? []
        };
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static Guid CreateDeterministicGuid(string input)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
```

### Step 7: Pipeline Orchestration

```csharp
// IndexingPipeline.cs
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodeIntelligence.Pipeline;

public class IndexingPipeline
{
    private readonly ICodeExtractor _extractor;
    private readonly ISemanticEnricher _enricher;
    private readonly IEmbeddingProvider _embedder;
    private readonly IVectorStore _store;
    private readonly ILogger<IndexingPipeline> _logger;
    private readonly IndexingOptions _options;

    public IndexingPipeline(
        ICodeExtractor extractor,
        ISemanticEnricher enricher,
        IEmbeddingProvider embedder,
        IVectorStore store,
        ILogger<IndexingPipeline> logger,
        IndexingOptions? options = null)
    {
        _extractor = extractor;
        _enricher = enricher;
        _embedder = embedder;
        _store = store;
        _logger = logger;
        _options = options ?? new IndexingOptions();
    }

    public async Task IndexDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting indexing of {Directory}", directoryPath);

        await _store.InitializeAsync(cancellationToken);

        var chunks = new List<CodeChunk>();
        var filePatterns = _options.FilePatterns ?? ["*.cs", "*.ts", "*.py"];

        // Phase 1: Extract code chunks
        _logger.LogInformation("Phase 1: Extracting code chunks...");
        await foreach (var chunk in _extractor.ExtractFromDirectoryAsync(
            directoryPath, filePatterns, cancellationToken))
        {
            chunks.Add(chunk);

            if (chunks.Count % 100 == 0)
            {
                _logger.LogInformation("Extracted {Count} chunks...", chunks.Count);
            }
        }
        _logger.LogInformation("Extracted {Count} total chunks", chunks.Count);

        // Phase 2: Enrich chunks with semantic data
        _logger.LogInformation("Phase 2: Enriching chunks with semantic data...");
        var enrichedChunks = new List<EnrichedChunk>();

        foreach (var batch in chunks.Chunk(_options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var enriched = await _enricher.EnrichBatchAsync(
                batch.ToList(), cancellationToken);
            enrichedChunks.AddRange(enriched);

            _logger.LogInformation("Enriched {Count}/{Total} chunks",
                enrichedChunks.Count, chunks.Count);
        }

        // Phase 3: Generate embeddings
        _logger.LogInformation("Phase 3: Generating embeddings...");
        var embeddingInputs = enrichedChunks
            .Select(c => c.BuildEmbeddingInput())
            .ToList();

        var allEmbeddings = new List<float[]>();
        foreach (var batch in embeddingInputs.Chunk(_options.EmbeddingBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embeddings = await _embedder.EmbedBatchAsync(
                batch.ToList(), cancellationToken);
            allEmbeddings.AddRange(embeddings);

            _logger.LogInformation("Generated {Count}/{Total} embeddings",
                allEmbeddings.Count, embeddingInputs.Count);
        }

        // Phase 4: Store in vector database
        _logger.LogInformation("Phase 4: Storing in vector database...");
        var items = enrichedChunks
            .Zip(allEmbeddings, (chunk, embedding) => (chunk, embedding))
            .ToList();

        foreach (var batch in items.Chunk(_options.StoreBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _store.UpsertBatchAsync(batch.ToList(), cancellationToken);
        }

        _logger.LogInformation("Indexing complete. Indexed {Count} chunks", chunks.Count);
    }

    public async Task IndexFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Indexing file: {File}", filePath);

        // Delete existing chunks for this file
        await _store.DeleteByFileAsync(filePath, cancellationToken);

        // Extract, enrich, embed, store
        var chunks = new List<CodeChunk>();
        await foreach (var chunk in _extractor.ExtractAsync(filePath, cancellationToken))
        {
            chunks.Add(chunk);
        }

        if (chunks.Count == 0)
        {
            _logger.LogInformation("No chunks extracted from {File}", filePath);
            return;
        }

        var enriched = await _enricher.EnrichBatchAsync(chunks, cancellationToken);
        var inputs = enriched.Select(c => c.BuildEmbeddingInput()).ToList();
        var embeddings = await _embedder.EmbedBatchAsync(inputs, cancellationToken);

        var items = enriched.Zip(embeddings, (c, e) => (c, e)).ToList();
        await _store.UpsertBatchAsync(items, cancellationToken);

        _logger.LogInformation("Indexed {Count} chunks from {File}", chunks.Count, filePath);
    }
}

public record IndexingOptions
{
    public string[]? FilePatterns { get; init; }
    public int BatchSize { get; init; } = 50;
    public int EmbeddingBatchSize { get; init; } = 20;
    public int StoreBatchSize { get; init; } = 100;
}

// SearchPipeline.cs
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Core.Models;

namespace CodeIntelligence.Pipeline;

public class SearchPipeline
{
    private readonly IEmbeddingProvider _embedder;
    private readonly IVectorStore _store;

    public SearchPipeline(IEmbeddingProvider embedder, IVectorStore store)
    {
        _embedder = embedder;
        _store = store;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit = 10,
        SearchFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var queryEmbedding = await _embedder.EmbedAsync(query, cancellationToken);
        return await _store.SearchAsync(queryEmbedding, limit, filter, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchSimilarAsync(
        EnrichedChunk chunk,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var input = chunk.BuildEmbeddingInput();
        var embedding = await _embedder.EmbedAsync(input, cancellationToken);

        // Exclude the chunk itself
        var results = await _store.SearchAsync(embedding, limit + 1, null, cancellationToken);
        return results.Where(r => r.Chunk.Id != chunk.Id).Take(limit).ToList();
    }
}
```

### Step 8: Sample Application

```csharp
// Program.cs
using CodeIntelligence.Core.Interfaces;
using CodeIntelligence.Embedding.Providers;
using CodeIntelligence.Enrichment.Lsp;
using CodeIntelligence.Extraction.Roslyn;
using CodeIntelligence.Pipeline;
using CodeIntelligence.Storage.Qdrant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();

// Configure logging
services.AddLogging(builder => builder
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

// Configure services
services.AddSingleton<ICodeExtractor, RoslynExtractor>();

services.AddSingleton<ISemanticEnricher>(sp =>
{
    var configs = new[]
    {
        new LspServerConfig
        {
            Name = "csharp",
            Languages = ["csharp", "cs"],
            Command = "csharp-ls",  // Or OmniSharp
            Args = [],
            RootMarkers = ["*.sln", "*.csproj"]
        },
        new LspServerConfig
        {
            Name = "typescript",
            Languages = ["typescript", "javascript"],
            Command = "typescript-language-server",
            Args = ["--stdio"],
            RootMarkers = ["package.json", "tsconfig.json"]
        },
        new LspServerConfig
        {
            Name = "powershell",
            Languages = ["powershell"],
            Command = "pwsh",
            Args = ["-NoLogo", "-NoProfile", "-Command",
                "Import-Module PowerShellEditorServices; Start-EditorServices -Stdio"],
            RootMarkers = ["*.ps1", "*.psm1"]
        }
    };
    return new LspEnricher(configs);
});

// Choose your embedding provider
var embeddingApiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
    ?? throw new InvalidOperationException("VOYAGE_API_KEY not set");
services.AddSingleton<IEmbeddingProvider>(
    new VoyageEmbeddingProvider(embeddingApiKey));

// Or use OpenAI:
// var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
// services.AddSingleton<IEmbeddingProvider>(new OpenAIEmbeddingProvider(openaiKey));

// Configure vector store
services.AddSingleton<IVectorStore>(
    new QdrantVectorStore(
        host: "localhost",
        port: 6334,
        collectionName: "code_index",
        dimensions: 1024));

// Configure pipelines
services.AddSingleton<IndexingPipeline>();
services.AddSingleton<SearchPipeline>();

var provider = services.BuildServiceProvider();

// Example: Index a repository
var indexer = provider.GetRequiredService<IndexingPipeline>();
await indexer.IndexDirectoryAsync(
    @"C:\Projects\MyProject",
    CancellationToken.None);

// Example: Search
var searcher = provider.GetRequiredService<SearchPipeline>();
var results = await searcher.SearchAsync(
    "authenticate user with OAuth",
    limit: 5);

Console.WriteLine("Search Results:");
foreach (var result in results)
{
    Console.WriteLine($"  [{result.Score:F3}] {result.Chunk.SymbolName}");
    Console.WriteLine($"         {result.Chunk.FilePath}:{result.Chunk.StartLine}");
    Console.WriteLine($"         {result.Chunk.Signature}");
    Console.WriteLine();
}
```

### Running Qdrant

```bash
# Start Qdrant with Docker
docker run -p 6333:6333 -p 6334:6334 \
    -v $(pwd)/qdrant_storage:/qdrant/storage \
    qdrant/qdrant

# Or use Qdrant Cloud for managed service
```

### Performance Optimizations

```csharp
// Parallel file processing
public async Task IndexDirectoryParallelAsync(
    string directoryPath,
    int maxParallelism = 4,
    CancellationToken cancellationToken = default)
{
    var files = Directory.GetFiles(directoryPath, "*.cs", SearchOption.AllDirectories);

    await Parallel.ForEachAsync(
        files,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken
        },
        async (file, ct) =>
        {
            await IndexFileAsync(file, ct);
        });
}

// Incremental indexing based on file changes
public async Task IndexChangedFilesAsync(
    string directoryPath,
    DateTime since,
    CancellationToken cancellationToken = default)
{
    var changedFiles = Directory
        .GetFiles(directoryPath, "*.cs", SearchOption.AllDirectories)
        .Where(f => File.GetLastWriteTimeUtc(f) > since);

    foreach (var file in changedFiles)
    {
        await IndexFileAsync(file, cancellationToken);
    }
}
```

---

## Practical Approach: Building on Existing Hypergraph Implementation

This section provides a practical implementation strategy based on an existing hypergraph-based code analysis system located at `/mnt/e/work/github/crp/workflow/src/ast`.

### Existing Architecture Overview

The current implementation is a sophisticated multi-language code analysis system:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    Existing Hypergraph Architecture                      │
│                                                                          │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────────────────┐  │
│  │ Ast.Csharp   │    │ Ast.PowerShell│    │   Ast.Core              │  │
│  │ (Roslyn)     │    │ (PS Parser)  │    │   (HypergraphNode,      │  │
│  │              │    │              │    │    Hyperedge, etc.)     │  │
│  └──────┬───────┘    └──────┬───────┘    └──────────────────────────┘  │
│         │                   │                                           │
│         └─────────┬─────────┘                                           │
│                   ▼                                                      │
│         ┌─────────────────────┐                                         │
│         │ RepositoryProcessor │                                         │
│         │ (Cross-language)    │                                         │
│         └─────────┬───────────┘                                         │
│                   ▼                                                      │
│         ┌─────────────────────┐                                         │
│         │   HypergraphData    │                                         │
│         │   (Nodes + Edges)   │                                         │
│         └─────────┬───────────┘                                         │
│                   │                                                      │
│         ┌─────────┴─────────┐                                           │
│         ▼                   ▼                                           │
│  ┌─────────────────┐ ┌─────────────────┐                               │
│  │  Ast.Embedding  │ │  Ast.GraphDb    │                               │
│  │  (LLamaSharp,   │ │  (Neo4j)        │                               │
│  │   HTTP, OpenAI) │ │                 │                               │
│  └────────┬────────┘ └─────────────────┘                               │
│           ▼                                                             │
│  ┌─────────────────┐                                                   │
│  │  Ast.VectorDb   │                                                   │
│  │  (Qdrant)       │                                                   │
│  └─────────────────┘                                                   │
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                      Ast.Query                                   │   │
│  │         (Hybrid: Graph + Semantic Search)                       │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

**Strengths of Current Implementation:**
- ✅ **Hypergraph model** preserves multi-arity relationships (method calls with ALL args)
- ✅ **Multi-language support** (C# via Roslyn, PowerShell via PS Parser)
- ✅ **Cross-language detection** (PowerShell → C# type references)
- ✅ **Dual storage** (Neo4j for structure, Qdrant for semantics)
- ✅ **Hybrid queries** combining graph and vector search
- ✅ **60+ edge types** capturing architectural patterns (DI, HTTP, logging, etc.)

**Areas for Enhancement:**
- 🔶 Embedding model (nomic-embed-text) can be upgraded
- 🔶 Embedding text generation can be enriched
- 🔶 Missing LSP-based type information
- 🔶 No SCIP integration for batch indexing

### Recommended Enhancements

#### 1. Upgrade Embedding Model

**Current**: `nomic-embed-text` (768 dimensions)

**Recommended Options:**

| Model | Dimensions | Performance | Cost | Integration |
|-------|------------|-------------|------|-------------|
| **Voyage-Code-3** | 1024 | Best for code | $0.06/1M | HTTP API |
| **CodeXEmbed-7B** | 4096 | SOTA accuracy | Self-host | LLamaSharp |
| **Jina-Code-v2** | 768 | Good balance | Self-host | LLamaSharp |

**Implementation - Add to Ast.Embedding:**

```csharp
// VoyageEmbeddingGenerator.cs
using System.Net.Http.Json;

namespace Ast.Embedding;

public class VoyageEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public int Dimensions => 1024;
    public string Name => "voyage-code-3";

    public VoyageEmbeddingGenerator(string apiKey, string model = "voyage-code-3")
    {
        _model = model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.voyageai.com/v1/")
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await GenerateBatchEmbeddingsAsync([text], cancellationToken);
        return result.First();
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            input = texts,
            model = _model,
            input_type = "document"  // Use "query" for search queries
        };

        var response = await _httpClient.PostAsJsonAsync(
            "embeddings", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<VoyageResponse>(
            cancellationToken: cancellationToken);

        return result!.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    private record VoyageResponse(VoyageEmbedding[] Data);
    private record VoyageEmbedding(int Index, float[] Embedding);
}
```

#### 2. Enhance Embedding Text Generation

**Current** (from `HypergraphNode.GetEmbeddingText()`):
```
"{NodeType}: {Name}\nLanguage: {Language}\nFile: {FilePath}:{LineStart}"
```

**Enhanced** - richer context for better embeddings:

```csharp
// Add to HypergraphNode.cs
public string GetEnhancedEmbeddingText()
{
    var parts = new List<string>();

    // 1. Symbol identification
    parts.Add($"{NodeType}: {Name}");

    // 2. Documentation (if available)
    if (Metadata.TryGetValue("Documentation", out var doc) && !string.IsNullOrEmpty(doc))
    {
        parts.Add(doc);
    }

    // 3. Signature (for methods/properties)
    if (Metadata.TryGetValue("Signature", out var sig) && !string.IsNullOrEmpty(sig))
    {
        parts.Add($"Signature: {sig}");
    }

    // 4. Return type
    if (Metadata.TryGetValue("ReturnType", out var ret) && !string.IsNullOrEmpty(ret))
    {
        parts.Add($"Returns: {ret}");
    }

    // 5. Parameters (for methods)
    if (Metadata.TryGetValue("Parameters", out var param) && !string.IsNullOrEmpty(param))
    {
        parts.Add($"Parameters: {param}");
    }

    // 6. Scope and modifiers
    var context = new List<string>();
    if (!string.IsNullOrEmpty(Scope)) context.Add(Scope);
    if (Metadata.TryGetValue("Modifiers", out var mod)) context.Add(mod);
    if (context.Count > 0)
    {
        parts.Add($"Context: {string.Join(", ", context)}");
    }

    // 7. Relationships (outgoing calls - truncated)
    if (Metadata.TryGetValue("Calls", out var calls) && !string.IsNullOrEmpty(calls))
    {
        var callList = calls.Split(',').Take(10);
        parts.Add($"Calls: {string.Join(", ", callList)}");
    }

    // 8. Actual code content (truncated)
    if (!string.IsNullOrEmpty(SourceCode))
    {
        var truncated = SourceCode.Length > 1500
            ? SourceCode[..1500] + "..."
            : SourceCode;
        parts.Add(truncated);
    }

    return string.Join("\n", parts);
}
```

#### 3. Add LSP Enrichment Service

Create a new project `Ast.Lsp` for extracting semantic information:

```csharp
// Ast.Lsp/LspEnrichmentService.cs
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Ast.Lsp;

public class LspEnrichmentService : IAsyncDisposable
{
    private readonly Dictionary<string, LspClient> _clients = new();
    private readonly LspServerConfig[] _serverConfigs;

    public LspEnrichmentService(LspServerConfig[]? configs = null)
    {
        _serverConfigs = configs ?? DefaultConfigs;
    }

    private static LspServerConfig[] DefaultConfigs =>
    [
        new("csharp", ["csharp"], "csharp-ls", [],
            ["*.sln", "*.csproj"]),
        new("powershell", ["powershell"], "pwsh",
            ["-NoLogo", "-NoProfile", "-Command",
             "Import-Module PowerShellEditorServices; Start-EditorServices -Stdio"],
            ["*.ps1", "*.psm1", "*.psd1"]),
    ];

    /// <summary>
    /// Enrich a HypergraphNode with LSP-derived semantic information
    /// </summary>
    public async Task<HypergraphNode> EnrichNodeAsync(
        HypergraphNode node,
        CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateClientAsync(node.Language, node.FilePath, cancellationToken);
        if (client is null) return node;

        try
        {
            // Get hover info for type signatures
            var hover = await client.GetHoverAsync(
                node.FilePath, node.LineStart, 0, cancellationToken);

            if (hover is not null)
            {
                var signature = ExtractSignature(hover);
                if (!string.IsNullOrEmpty(signature))
                {
                    node = node with
                    {
                        Metadata = new Dictionary<string, string>(node.Metadata)
                        {
                            ["Signature"] = signature
                        }
                    };
                }
            }

            // Get references for call graph
            var references = await client.GetReferencesAsync(
                node.FilePath, node.LineStart, 0, cancellationToken);

            if (references.Any())
            {
                var refList = references
                    .Select(r => $"{Path.GetFileName(r.Uri.Path)}:{r.Range.Start.Line + 1}")
                    .Take(20)
                    .ToList();

                node = node with
                {
                    Metadata = new Dictionary<string, string>(node.Metadata)
                    {
                        ["CalledBy"] = string.Join(",", refList)
                    }
                };
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - LSP is best-effort enrichment
            Console.WriteLine($"LSP enrichment failed for {node.Id}: {ex.Message}");
        }

        return node;
    }

    /// <summary>
    /// Batch enrich nodes - groups by file for efficiency
    /// </summary>
    public async Task<IReadOnlyList<HypergraphNode>> EnrichNodesAsync(
        IReadOnlyList<HypergraphNode> nodes,
        IProgress<EnrichmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var enriched = new List<HypergraphNode>();
        var byFile = nodes.GroupBy(n => n.FilePath);
        var total = nodes.Count;
        var processed = 0;

        foreach (var group in byFile)
        {
            foreach (var node in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await EnrichNodeAsync(node, cancellationToken);
                enriched.Add(result);
                processed++;

                progress?.Report(new EnrichmentProgress(processed, total, node.FilePath));
            }
        }

        return enriched;
    }

    private async Task<LspClient?> GetOrCreateClientAsync(
        string language,
        string filePath,
        CancellationToken cancellationToken)
    {
        var config = _serverConfigs.FirstOrDefault(c =>
            c.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));

        if (config is null) return null;

        var rootPath = FindProjectRoot(filePath, config.RootMarkers);
        var key = $"{config.Name}:{rootPath}";

        if (!_clients.TryGetValue(key, out var client))
        {
            client = new LspClient(config.Command, config.Args, rootPath);
            await client.InitializeAsync(cancellationToken);
            _clients[key] = client;
        }

        return client;
    }

    private static string FindProjectRoot(string filePath, string[] markers)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (markers.Any(m =>
                Directory.GetFiles(dir, m).Length > 0 ||
                Directory.GetDirectories(dir, m).Length > 0))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetDirectoryName(filePath)!;
    }

    private static string? ExtractSignature(Hover hover)
    {
        // Extract code block from hover markdown
        var content = hover.Contents.MarkupContent?.Value
                   ?? hover.Contents.MarkedStrings?.FirstOrDefault()?.Value;

        if (content is null) return null;

        // Extract first code block
        var lines = content.Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("```"))
            {
                if (inCodeBlock) break;
                inCodeBlock = true;
                continue;
            }
            if (inCodeBlock)
            {
                codeLines.Add(line);
            }
        }

        return codeLines.Count > 0
            ? string.Join("\n", codeLines).Trim()
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
        _clients.Clear();
    }
}

public record LspServerConfig(
    string Name,
    string[] Languages,
    string Command,
    string[] Args,
    string[] RootMarkers);

public record EnrichmentProgress(int Processed, int Total, string CurrentFile)
{
    public double Percentage => Total > 0 ? (double)Processed / Total * 100 : 0;
}
```

#### 4. Enhanced Pipeline with LSP Enrichment

Update the `CodeEmbeddingService` to incorporate LSP enrichment:

```csharp
// Enhanced CodeEmbeddingService.cs
namespace Ast.Embedding;

public class EnhancedCodeEmbeddingService
{
    private readonly IEmbeddingGenerator _embedder;
    private readonly LspEnrichmentService? _lspEnricher;
    private readonly EmbeddingOptions _options;

    public EnhancedCodeEmbeddingService(
        IEmbeddingGenerator embedder,
        LspEnrichmentService? lspEnricher = null,
        EmbeddingOptions? options = null)
    {
        _embedder = embedder;
        _lspEnricher = lspEnricher;
        _options = options ?? new EmbeddingOptions();
    }

    public async Task<IReadOnlyList<CodeEmbedding>> GenerateEnrichedEmbeddingsAsync(
        HypergraphData hypergraph,
        IProgress<EmbeddingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var nodes = hypergraph.Nodes.ToList();
        var results = new List<CodeEmbedding>();

        // Phase 1: LSP Enrichment (optional)
        if (_lspEnricher is not null && _options.UseLspEnrichment)
        {
            progress?.Report(new EmbeddingProgress("Enriching with LSP data...", 0, nodes.Count));

            var enrichmentProgress = new Progress<EnrichmentProgress>(p =>
            {
                progress?.Report(new EmbeddingProgress(
                    $"LSP enrichment: {p.CurrentFile}",
                    p.Processed,
                    p.Total));
            });

            nodes = (await _lspEnricher.EnrichNodesAsync(
                nodes, enrichmentProgress, cancellationToken)).ToList();
        }

        // Phase 2: Generate embedding texts
        progress?.Report(new EmbeddingProgress("Generating embedding texts...", 0, nodes.Count));

        var embeddingInputs = nodes
            .Select(n => (Node: n, Text: n.GetEnhancedEmbeddingText()))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();

        // Phase 3: Batch embed
        var batchSize = _options.BatchSize;
        var batches = embeddingInputs.Chunk(batchSize).ToList();
        var allEmbeddings = new List<float[]>();
        var processed = 0;

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var texts = batch.Select(x => x.Text).ToList();
            var embeddings = await _embedder.GenerateBatchEmbeddingsAsync(
                texts, cancellationToken);

            allEmbeddings.AddRange(embeddings);
            processed += batch.Length;

            progress?.Report(new EmbeddingProgress(
                "Generating embeddings...",
                processed,
                embeddingInputs.Count));
        }

        // Phase 4: Create CodeEmbedding objects
        for (int i = 0; i < embeddingInputs.Count; i++)
        {
            var (node, text) = embeddingInputs[i];
            results.Add(new CodeEmbedding
            {
                NodeId = node.Id,
                Embedding = allEmbeddings[i],
                SourceText = text,
                Language = node.Language,
                FilePath = node.FilePath,
                LineStart = node.LineStart,
                LineEnd = node.LineEnd
            });
        }

        return results;
    }
}

public record EmbeddingOptions
{
    public int BatchSize { get; init; } = 32;
    public bool UseLspEnrichment { get; init; } = true;
    public int MaxTextLength { get; init; } = 2000;
}

public record EmbeddingProgress(string Status, int Processed, int Total)
{
    public double Percentage => Total > 0 ? (double)Processed / Total * 100 : 0;
}
```

#### 5. SCIP Integration for Batch Indexing

For C# projects, add SCIP support as an alternative to LSP:

```csharp
// Ast.Scip/ScipEnricher.cs
using Google.Protobuf;

namespace Ast.Scip;

public class ScipEnricher
{
    private readonly string _indexPath;
    private ScipIndex? _index;

    public ScipEnricher(string indexPath)
    {
        _indexPath = indexPath;
    }

    /// <summary>
    /// Generate SCIP index using scip-dotnet
    /// </summary>
    public async Task GenerateIndexAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "scip-dotnet",
                Arguments = $"index --output \"{_indexPath}\" \"{projectPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"SCIP indexing failed: {error}");
        }
    }

    /// <summary>
    /// Load and parse SCIP index
    /// </summary>
    public async Task LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_indexPath))
        {
            throw new FileNotFoundException("SCIP index not found", _indexPath);
        }

        var bytes = await File.ReadAllBytesAsync(_indexPath, cancellationToken);
        _index = ScipIndex.Parser.ParseFrom(bytes);
    }

    /// <summary>
    /// Enrich nodes with SCIP symbol information
    /// </summary>
    public IReadOnlyList<HypergraphNode> EnrichNodes(IReadOnlyList<HypergraphNode> nodes)
    {
        if (_index is null)
        {
            throw new InvalidOperationException("SCIP index not loaded");
        }

        var enriched = new List<HypergraphNode>();
        var symbolMap = BuildSymbolMap();

        foreach (var node in nodes)
        {
            var key = $"{node.FilePath}:{node.LineStart}";
            if (symbolMap.TryGetValue(key, out var scipSymbol))
            {
                var newMetadata = new Dictionary<string, string>(node.Metadata)
                {
                    ["ScipSymbol"] = scipSymbol.Symbol,
                    ["Signature"] = scipSymbol.Signature,
                    ["Documentation"] = scipSymbol.Documentation
                };

                if (scipSymbol.References.Count > 0)
                {
                    newMetadata["References"] = string.Join(",",
                        scipSymbol.References.Take(20));
                }

                enriched.Add(node with { Metadata = newMetadata });
            }
            else
            {
                enriched.Add(node);
            }
        }

        return enriched;
    }

    private Dictionary<string, ScipSymbolInfo> BuildSymbolMap()
    {
        var map = new Dictionary<string, ScipSymbolInfo>();

        foreach (var doc in _index!.Documents)
        {
            foreach (var occurrence in doc.Occurrences)
            {
                if (string.IsNullOrEmpty(occurrence.Symbol)) continue;
                if ((occurrence.SymbolRoles & SymbolRole.Definition) == 0) continue;

                var line = occurrence.Range.Count > 0 ? occurrence.Range[0] : 0;
                var key = $"{doc.RelativePath}:{line + 1}";

                var symbolInfo = _index.Symbols
                    .FirstOrDefault(s => s.Symbol == occurrence.Symbol);

                map[key] = new ScipSymbolInfo
                {
                    Symbol = occurrence.Symbol,
                    Signature = symbolInfo?.Signature ?? "",
                    Documentation = string.Join("\n",
                        symbolInfo?.Documentation ?? []),
                    References = FindReferences(occurrence.Symbol)
                };
            }
        }

        return map;
    }

    private List<string> FindReferences(string symbol)
    {
        var refs = new List<string>();

        foreach (var doc in _index!.Documents)
        {
            foreach (var occ in doc.Occurrences)
            {
                if (occ.Symbol == symbol &&
                    (occ.SymbolRoles & SymbolRole.Definition) == 0)
                {
                    var line = occ.Range.Count > 0 ? occ.Range[0] : 0;
                    refs.Add($"{doc.RelativePath}:{line + 1}");
                }
            }
        }

        return refs;
    }
}

public record ScipSymbolInfo
{
    public required string Symbol { get; init; }
    public required string Signature { get; init; }
    public required string Documentation { get; init; }
    public required List<string> References { get; init; }
}
```

### Complete Enhanced Pipeline

Here's how all components work together:

```csharp
// Example: Full indexing pipeline with all enhancements
public class EnhancedIndexingPipeline
{
    public async Task IndexRepositoryAsync(
        string repoPath,
        IndexingConfiguration config,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Phase 1: Extract hypergraph using existing extractors
        progress?.Report(new PipelineProgress("Extracting code structure...", 0));

        var processor = new RepositoryProcessor(config.AstConfig);
        var hypergraph = await processor.ProcessRepositoryAsync(repoPath, cancellationToken);

        progress?.Report(new PipelineProgress(
            $"Extracted {hypergraph.Nodes.Count} nodes, {hypergraph.Edges.Count} edges", 10));

        // Phase 2: Enrich with SCIP (for C# if available)
        if (config.UseScip && File.Exists(config.ScipIndexPath))
        {
            progress?.Report(new PipelineProgress("Enriching with SCIP data...", 20));

            var scipEnricher = new ScipEnricher(config.ScipIndexPath);
            await scipEnricher.LoadIndexAsync(cancellationToken);
            hypergraph = hypergraph with
            {
                Nodes = scipEnricher.EnrichNodes(hypergraph.Nodes.ToList())
            };
        }

        // Phase 3: Enrich with LSP (for languages without SCIP)
        if (config.UseLsp)
        {
            progress?.Report(new PipelineProgress("Enriching with LSP data...", 30));

            await using var lspEnricher = new LspEnrichmentService();
            var enrichedNodes = await lspEnricher.EnrichNodesAsync(
                hypergraph.Nodes.ToList(),
                cancellationToken: cancellationToken);

            hypergraph = hypergraph with { Nodes = enrichedNodes };
        }

        // Phase 4: Generate embeddings with enhanced text
        progress?.Report(new PipelineProgress("Generating embeddings...", 50));

        var embedder = CreateEmbedder(config);
        var embeddingService = new EnhancedCodeEmbeddingService(embedder);
        var embeddings = await embeddingService.GenerateEnrichedEmbeddingsAsync(
            hypergraph, cancellationToken: cancellationToken);

        // Phase 5: Store in Neo4j (graph structure)
        progress?.Report(new PipelineProgress("Storing in graph database...", 70));

        var graphStore = new Neo4jGraphStore(config.Neo4jUri, config.Neo4jAuth);
        await graphStore.StoreHypergraphAsync(hypergraph, cancellationToken);

        // Phase 6: Store in Qdrant (vector embeddings)
        progress?.Report(new PipelineProgress("Storing in vector database...", 85));

        var vectorStore = new QdrantVectorStore(
            config.QdrantHost, config.QdrantPort,
            dimensions: embedder.Dimensions);
        await vectorStore.InitializeAsync(cancellationToken);
        await vectorStore.StoreEmbeddingsAsync(embeddings, cancellationToken);

        progress?.Report(new PipelineProgress("Indexing complete!", 100));
    }

    private static IEmbeddingGenerator CreateEmbedder(IndexingConfiguration config)
    {
        return config.EmbeddingProvider switch
        {
            "voyage" => new VoyageEmbeddingGenerator(config.VoyageApiKey!),
            "openai" => new OpenAIEmbeddingGenerator(config.OpenAIApiKey!),
            "local" => new LLamaEmbeddingGenerator(config.LocalModelPath!),
            _ => new HttpEmbeddingGenerator(config.EmbeddingEndpoint!)
        };
    }
}

public record IndexingConfiguration
{
    public required AstConfiguration AstConfig { get; init; }

    // Enrichment options
    public bool UseScip { get; init; } = true;
    public bool UseLsp { get; init; } = true;
    public string? ScipIndexPath { get; init; }

    // Embedding provider
    public string EmbeddingProvider { get; init; } = "voyage";
    public string? VoyageApiKey { get; init; }
    public string? OpenAIApiKey { get; init; }
    public string? LocalModelPath { get; init; }
    public string? EmbeddingEndpoint { get; init; }

    // Database connections
    public required string Neo4jUri { get; init; }
    public required string Neo4jAuth { get; init; }
    public required string QdrantHost { get; init; }
    public int QdrantPort { get; init; } = 6334;
}

public record PipelineProgress(string Status, int Percentage);
```

### Recommended Migration Path

```
┌─────────────────────────────────────────────────────────────────────────┐
│                     Migration Path (Phased Approach)                     │
│                                                                          │
│  Phase 1: Upgrade Embedding Model (1-2 days)                            │
│  ├── Add VoyageEmbeddingGenerator                                       │
│  ├── Update EmbeddingModels configuration                               │
│  └── Re-index existing data with new embeddings                         │
│                                                                          │
│  Phase 2: Enhance Embedding Text (1 day)                                │
│  ├── Add GetEnhancedEmbeddingText() to HypergraphNode                   │
│  ├── Include more context (signatures, docs, relationships)            │
│  └── Test retrieval quality improvement                                 │
│                                                                          │
│  Phase 3: Add SCIP Integration (3-5 days)                               │
│  ├── Create Ast.Scip project                                            │
│  ├── Implement ScipEnricher                                             │
│  ├── Add to pipeline for C# projects                                    │
│  └── Compare with LSP enrichment                                        │
│                                                                          │
│  Phase 4: Add LSP Enrichment (1 week)                                   │
│  ├── Create Ast.Lsp project                                             │
│  ├── Implement LspClient and LspEnrichmentService                       │
│  ├── Configure for C# (csharp-ls) and PowerShell (PSES)                │
│  └── Integrate into pipeline                                            │
│                                                                          │
│  Phase 5: Optimize Hybrid Search (3-5 days)                             │
│  ├── Tune vector search parameters                                      │
│  ├── Add re-ranking based on graph proximity                            │
│  └── Implement query-time enrichment                                    │
└─────────────────────────────────────────────────────────────────────────┘
```

### Performance Considerations

| Component | Current | Recommended | Improvement |
|-----------|---------|-------------|-------------|
| **Embedding Model** | nomic-embed-text (768d) | Voyage-Code-3 (1024d) | +15% retrieval accuracy |
| **Embedding Text** | Basic metadata | Rich context | +20% relevance |
| **Type Info** | None | LSP/SCIP | Precise type matching |
| **Batch Size** | 32 | 20-50 (API-dependent) | Optimal throughput |
| **Vector Index** | HNSW default | HNSW m=16, ef=100 | Better recall |

---

## Summary: Decision Tree

```
                        Start
                          │
                          ▼
                ┌─────────────────┐
                │ Repo size < 10K │
                │     lines?      │
                └────────┬────────┘
                    Yes  │  No
              ┌──────────┴──────────┐
              ▼                     ▼
    ┌─────────────────┐   ┌─────────────────┐
    │  Simple AST +   │   │ Languages have  │
    │   Embeddings    │   │ SCIP support?   │
    │                 │   └────────┬────────┘
    │ • Tree-sitter   │       Yes  │  No
    │ • OpenAI small  │   ┌────────┴────────┐
    │ • Chroma        │   ▼                 ▼
    └─────────────────┘ ┌───────────┐  ┌───────────┐
                        │   SCIP +  │  │  LSP +    │
                        │ Embeddings│  │ Embeddings│
                        │           │  │           │
                        │• scip-*   │  │• LSP batch│
                        │• Voyage   │  │• Voyage   │
                        │• Qdrant   │  │• Qdrant   │
                        └───────────┘  └───────────┘
```

---

## Sources

### Embedding Models
- [6 Best Code Embedding Models Compared](https://modal.com/blog/6-best-code-embedding-models-compared)
- [CodeXEmbed Paper](https://arxiv.org/html/2411.12644v2)
- [Voyage-Code Documentation](https://docs.voyageai.com/docs/code-embeddings)

### Chunking
- [AST-Aware Code Chunking](https://supermemory.ai/blog/building-code-chunk-ast-aware-code-chunking/)
- [cAST: Structural Chunking via AST](https://arxiv.org/html/2506.15655v1)
- [code-chunk Library](https://github.com/supermemoryai/code-chunk)

### Indexing
- [SCIP Protocol](https://github.com/sourcegraph/scip)
- [LSIF Overview](https://microsoft.github.io/language-server-protocol/overviews/lsif/overview/)
- [scip-dotnet](https://github.com/sourcegraph/scip-dotnet)

### Storage
- [Vector Database Comparison 2025](https://tensorblue.com/blog/vector-database-comparison-pinecone-weaviate-qdrant-milvus-2025)
- [Qdrant Documentation](https://qdrant.tech/documentation/)
- [RAG for Large-Scale Code Repos](https://www.qodo.ai/blog/rag-for-large-scale-code-repos/)

### Implementation Examples
- [Building a Cursor-like AI Code Assistant](https://blogs.penify.dev/docs/building-cursor-like-ai-code-assistant.html)
- [Code Search with Qdrant](https://qdrant.tech/documentation/advanced-tutorials/code-search/)
- [RAG on Codebases with LanceDB](https://blog.lancedb.com/rag-codebase-1/)
