# Graph Schema Contract

> Canonical schema version: **1.0.0**

This document describes the graph schema contract consumed by all downstream
workstreams (extractors, storage, retrieval). The authoritative TypeScript
types are in `src/schema/types.ts`; this document provides a narrative
overview.

## Package

```
npm install @agent-memory/graph-schema
```

```typescript
import {
  GraphNode, GraphEdge, NodeKind, EdgeKind,
  buildNodeId, fileNodeId, symbolNodeId,
  validateNode, upsertNode,
} from '@agent-memory/graph-schema';
```

---

## Node Types

| Kind | Value | Description | Properties |
|------|-------|-------------|------------|
| Repository | `repository` | Root of the graph | `name`, `url?`, `defaultBranch?` |
| Package | `package` | A package/module within a monorepo | `name`, `path`, `language?`, `version?` |
| Directory | `directory` | A directory in the file tree | `name`, `path` |
| File | `file` | A source file | `name`, `path`, `language?`, `lineCount?`, `checksum?` |
| Class | `class` | A class declaration | (symbol properties) |
| Function | `function` | A function declaration | (symbol properties) |
| Method | `method` | A method within a class | (symbol properties) |
| Interface | `interface` | An interface/protocol | (symbol properties) |
| TypeAlias | `type_alias` | A type alias/typedef | (symbol properties) |
| Enum | `enum` | An enum declaration | (symbol properties) |
| Variable | `variable` | A variable binding | (symbol properties) |
| Constant | `constant` | A constant binding | (symbol properties) |
| Namespace | `namespace` | A namespace/module scope | (symbol properties) |

### Symbol Properties

All symbol nodes (`class` through `namespace`) share these properties:

```typescript
interface SymbolProperties {
  name: string;           // Simple name
  qualifiedName: string;  // Fully qualified name
  signature?: string;     // Type signature
  visibility?: Visibility;
  docComment?: string;
  isAsync?: boolean;
  isStatic?: boolean;
  isAbstract?: boolean;
  language?: string;
}
```

---

## Edge Types

### Structural Edges

| Kind | Value | From → To | Description |
|------|-------|-----------|-------------|
| Contains | `contains` | Container → Child | File system or scope containment |
| Imports | `imports` | File → File/Symbol | Import dependency |
| Exports | `exports` | File → Symbol | Public export |
| ReExports | `re_exports` | File → Symbol | Re-exported symbol |
| DependsOn | `depends_on` | Package → Package | Package dependency |

### Call-Chain Edges

| Kind | Value | From → To | Description |
|------|-------|-----------|-------------|
| Calls | `calls` | Fn/Method → Fn/Method | Function call |
| Extends | `extends` | Class → Class | Class inheritance |
| Implements | `implements` | Class → Interface | Interface implementation |
| References | `references` | Symbol → Symbol | Read/write reference |
| TypeRef | `type_ref` | Symbol → Type | Type annotation reference |
| Instantiates | `instantiates` | Fn/Method → Class | Constructor call |

---

## Canonical ID Format

### Node IDs

Grammar: `{kind}:{repo_id}/{path_segments}#{symbol_chain}`

```
repository:a1b2c3d4e5f6
file:a1b2c3d4e5f6/src/auth/service.ts
function:a1b2c3d4e5f6/src/auth/service.ts#authenticate
method:a1b2c3d4e5f6/src/auth/service.ts#AuthService#login
```

- **`repo_id`**: 12-char hex FNV-1a hash of the normalized clone URL
- **Path segments**: `/`-separated, percent-encoded
- **Symbol chain**: `#`-separated for nested symbols (class → method)
- **Reserved chars**: `:`, `#`, `%` are percent-encoded within segments

### Edge IDs

Grammar: `edge:{hash16}`

The 16-char hex hash is FNV-1a of `sourceId\0targetId\0kind[\0span]`.
When a `sourceSpan` is provided, it's included in the hash to
distinguish multiple edges of the same kind between the same nodes
(e.g., two call sites).

### Convenience Builders

```typescript
repositoryNodeId(repoId)
fileNodeId(repoId, 'src/auth/service.ts')
directoryNodeId(repoId, 'src/auth')
symbolNodeId(NodeKind.Function, repoId, 'src/auth/service.ts', ['authenticate'])
packageNodeId(repoId, 'packages/auth')
buildEdgeId(sourceId, targetId, EdgeKind.Calls, sourceSpan?)
```

---

## Provenance Model

Every node and edge carries an `observations` array:

```typescript
interface Observation {
  extractorName: string;      // e.g., 'repo-structure'
  extractorVersion: string;   // semver, e.g., '1.2.0'
  analysisBackend: AnalysisBackend; // 'lsp' | 'tree_sitter' | ...
  confidenceTier: ConfidenceTier;   // 'exact' | 'heuristic' | 'unresolved'
  confidence: number;         // 0.0–1.0
  commitSha: string;          // git commit at extraction time
  timestamp: string;          // ISO 8601
  sourceFile: string;         // file that was analyzed
  sourceSpan?: SourceSpan;    // location in the source file
}
```

### Multi-Extractor Accumulation

- Same `extractorName` + `extractorVersion` → **replaces** prior observation
- Different extractor → **appends** to the observations array
- Effective confidence = `max(observations.map(o => o.confidence))`

---

## Upsert Semantics

| Merge Rule | Fields | Behavior |
|------------|--------|----------|
| **Immutable** | `id`, `kind`, `createdAt`, `sourceId`, `targetId` | Never modified |
| **Latest-wins** | `properties`, `sourceSpan`, `filePath` | Replaced by incoming |
| **Accumulate** | `observations` | Appended (same extractor+version replaces) |
| **Derived** | `staleness`, `lastSeenAt` | Computed from observations |

```typescript
// First observation creates the node
const node = upsertNode(null, incomingNode);

// Subsequent observations merge into existing
const updated = upsertNode(existingNode, incomingNode);
```

---

## Deletion Semantics

### Lifecycle States

```
current → stale → deleted
```

| State | Meaning | Retrieval |
|-------|---------|-----------|
| `current` | Confirmed in latest extraction for its scope | Included |
| `stale` | Not seen in latest extraction; may still exist | Included (flagged) |
| `deleted` | Explicitly removed; pending physical cleanup | Excluded |

### Scope-Bounded Staleness

Each extraction declares an `ExtractionScope`:

```typescript
interface ExtractionScope {
  extractorName: string;
  includedPaths: string[];       // glob patterns
  excludedPaths?: string[];      // glob patterns
  ownedNodeKinds: NodeKind[];    // kinds this extractor is authoritative for
  ownedEdgeKinds: EdgeKind[];
}
```

After extraction completes:
1. Entities within scope that were observed → `staleness = 'current'`
2. Entities within scope that were **not** observed → `staleness = 'stale'`
3. Entities outside scope → **untouched** (another extractor owns them)

This prevents a repo-structure extractor from marking call-chain edges
as stale, and vice versa.

### Soft Delete

```typescript
const deleted = softDeleteNode(node);  // staleness → 'deleted'
```

Soft-deleted entities are excluded from retrieval but retained in storage
for a configurable retention period. This allows recovery from incorrect
deletions.

---

## Validation

Runtime validation is provided for all schema entities:

```typescript
const result = validateNode(node);
if (!result.valid) {
  console.error(result.errors);
}

const edgeResult = validateEdge(edge);
const extractionResult = validateExtractionResult(result);
```

Validation checks:
- Canonical ID format
- Node/edge kind is in the known enum
- Staleness value is valid
- Symbol nodes have `qualifiedName` and `name`
- Source spans have valid ranges
- Non-deleted entities have at least one observation
- Observations have valid semver versions, confidence in [0, 1], etc.
- Extraction results have valid schema version, timestamps, and
  referential integrity (edge source/target nodes exist in the result)

---

## Extraction Result Contract

Every extractor emits an `ExtractionResult`:

```typescript
interface ExtractionResult {
  schemaVersion: '1.0.0';
  extractionId: string;
  scope: ExtractionScope;
  commitSha: string;
  startedAt: string;
  completedAt: string;
  languages: string[];
  nodes: GraphNode[];
  edges: GraphEdge[];
  processedFiles: string[];
  errors?: ExtractionError[];
}
```

This is the API contract between extractors and the ingestion pipeline.
The pipeline validates the result, applies upsert semantics, and
computes staleness for entities within the declared scope.
