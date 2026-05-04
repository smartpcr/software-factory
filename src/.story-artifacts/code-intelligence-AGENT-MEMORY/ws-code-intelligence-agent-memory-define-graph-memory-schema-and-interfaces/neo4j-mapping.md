# Neo4j Label and Relationship Type Mapping

This document defines the canonical mapping between the graph memory schema's
`NodeKind` / `EdgeKind` enums and Neo4j labels and relationship types.

Downstream persistence implementations (targeting Neo4j) **must** use these
labels and relationship types to ensure interoperability.

## Schema Version

`0.3.0`

---

## Node Labels

Each `GraphNode` maps to a Neo4j node with:
- A **primary label** derived from the `kind` field (PascalCase).
- A **layer label** derived from the optional `layer` field.
- All `properties` bag fields stored as Neo4j node properties (camelCase).
- Common fields (`id`, `name`, `qualifiedName`, `repoId`, `version`, etc.)
  stored as top-level Neo4j properties.
- The `id` field is the unique constraint key.

| `NodeKind` (wire) | Neo4j Primary Label | Neo4j Layer Label |
|--------------------|--------------------|--------------------|
| `workspace`        | `Workspace`        | `Structural`       |
| `repo`             | `Repo`             | `Structural`       |
| `package`          | `Package`          | `Structural`       |
| `module`           | `Module`           | `Structural`       |
| `directory`        | `Directory`        | `Structural`       |
| `file`             | `File`             | `Structural`       |
| `class`            | `Class`            | `Structural`       |
| `interface`        | `Interface`        | `Structural`       |
| `type_alias`       | `TypeAlias`        | `Structural`       |
| `function`         | `Function`         | `Structural`       |
| `method`           | `Method`           | `Structural`       |
| `variable`         | `Variable`         | `Structural`       |
| `enum`             | `Enum`             | `Structural`       |
| `symbol`           | `Symbol`           | `Structural`       |
| `semantic_cluster` | `SemanticCluster`  | `Semantic`         |
| `topic`            | `Topic`            | `Semantic`         |
| `expertise_note`   | `ExpertiseNote`    | `Expertise`        |
| `pattern`          | `Pattern`          | `Expertise`        |
| `pitfall`          | `Pitfall`          | `Expertise`        |
| `preference`       | `Preference`       | `Expertise`        |
| `insight`          | `Insight`          | `Expertise`        |

### Node property storage

```cypher
CREATE (n:File:Structural {
  id: "file-1",
  name: "app.ts",
  qualifiedName: "src/app.ts",
  repoId: "repo-1",
  version: 2,
  createdAt: datetime("2026-05-04T00:00:00Z"),
  updatedAt: datetime("2026-05-04T00:00:00Z"),
  stale: false,
  contentHash: "sha256:abc",
  // FileProperties fields (flattened):
  path: "src/app.ts",
  language: "typescript",
  loc: 120,
  hash: "sha256:abc"
})
```

### Embeddings

Store the `embedding` field as a Neo4j vector property for similarity search:

```cypher
CALL db.index.vector.createNodeIndex(
  'graph_memory_embedding', 'File', 'embedding', 1536, 'cosine'
)
```

### Unique constraint

```cypher
CREATE CONSTRAINT graph_memory_node_id IF NOT EXISTS
FOR (n:GraphMemoryNode)
REQUIRE n.id IS UNIQUE
```

All nodes should also carry the meta-label `GraphMemoryNode` so the constraint applies universally.

---

## Relationship Types

Each `GraphEdge` maps to a Neo4j relationship with:
- A **relationship type** derived from the `kind` field (SCREAMING_SNAKE_CASE).
- All `properties` bag fields stored as relationship properties (camelCase).
- Common fields (`id`, `version`, `layer`, `sourceRepoId`, `targetRepoId`, etc.)
  stored as relationship properties.

| `EdgeKind` (wire)       | Neo4j Relationship Type  |
|-------------------------|--------------------------|
| `contains`              | `CONTAINS`               |
| `calls`                 | `CALLS`                  |
| `called_by`             | `CALLED_BY`              |
| `imports`               | `IMPORTS`                |
| `re_exports`            | `RE_EXPORTS`             |
| `exports`               | `EXPORTS`                |
| `inherits`              | `INHERITS`               |
| `overrides`             | `OVERRIDES`              |
| `implements`            | `IMPLEMENTS`             |
| `instantiates`          | `INSTANTIATES`           |
| `expertise`             | `EXPERTISE`              |
| `applies_to`            | `APPLIES_TO`             |
| `similar_to`            | `SIMILAR_TO`             |
| `belongs_to_cluster`    | `BELONGS_TO_CLUSTER`     |
| `supersedes`            | `SUPERSEDES`             |
| `derived_from`          | `DERIVED_FROM`           |
| `frequently_used_with`  | `FREQUENTLY_USED_WITH`   |
| `depends_on`            | `DEPENDS_ON`             |
| `references`            | `REFERENCES`             |

### Relationship property storage

```cypher
MATCH (a:Function {id: "fn-login"})
MATCH (b:Function {id: "fn-hash"})
CREATE (a)-[:CALLS {
  id: "e-001",
  version: 1,
  layer: "call_chain",
  // CallsProperties fields:
  callSiteLine: 25,
  callSiteColumn: 8,
  isConditional: false,
  isDynamic: false,
  isAsync: true,
  confidence: 0.95
}]->(b)
```

> **Note**: Neo4j does not support nested objects in relationship properties.
> Nested objects like `callSite` should be flattened with a prefix
> (e.g. `callSite.line` → `callSiteLine`, `callSite.column` → `callSiteColumn`).
> Arrays of objects (e.g. `importedSymbols`, `evidence`) should be stored as
> JSON strings or decomposed into separate relationships.

### Relationship unique constraint

```cypher
CREATE CONSTRAINT graph_memory_edge_id IF NOT EXISTS
FOR ()-[r:CALLS]-()
REQUIRE r.id IS UNIQUE
```

Apply similar constraints for all relationship types.

---

## Extension Properties

Extension properties (`extensions` field) use keys prefixed with `x-`.
In Neo4j, store them as node/relationship properties with the same key:

```cypher
SET n.`x-myTool` = "value"
```

---

## VersionRange Storage

The `versionRange` object is flattened in Neo4j:

```cypher
SET n.versionRangeIntroducedIn = "commit-abc"
SET n.versionRangeLastSeenIn = "commit-def"
SET n.versionRangeRemovedIn = "commit-ghi"
```

---

## Provenance Storage

The `provenance` object is flattened in Neo4j:

```cypher
SET n.provenanceAnalyzer = "ast-parser"
SET n.provenanceAnalyzerVersion = "2.0.0"
SET n.provenanceAnalyzedAt = datetime("2026-05-04T00:00:00Z")
SET n.provenanceCommitSha = "abc123"
```

---

## Snapshot Management

Graph snapshots can leverage Neo4j's built-in transaction log or be
implemented via `snapshotId` property filtering:

```cypher
// Query nodes at a specific snapshot
MATCH (n:GraphMemoryNode)
WHERE n.snapshotId = "snap-42" OR n.snapshotId IS NULL
RETURN n
```

---

## Index Recommendations

```cypher
// Primary lookup
CREATE INDEX graph_memory_node_id FOR (n:GraphMemoryNode) ON (n.id)

// Kind-based queries
CREATE INDEX graph_memory_node_kind FOR (n:GraphMemoryNode) ON (n.kind)

// Name search
CREATE INDEX graph_memory_node_name FOR (n:GraphMemoryNode) ON (n.name)

// Repo scoping
CREATE INDEX graph_memory_node_repo FOR (n:GraphMemoryNode) ON (n.repoId)

// Stale node cleanup
CREATE INDEX graph_memory_node_stale FOR (n:GraphMemoryNode) ON (n.stale)

// Vector similarity (requires Neo4j 5.11+)
CALL db.index.vector.createNodeIndex(
  'graph_memory_embedding', 'GraphMemoryNode', 'embedding', 1536, 'cosine'
)
```
