# Neo4j Label & Relationship-Type Mapping

This document defines the canonical mapping from the code knowledge graph
schema types to Neo4j labels and relationship types. All downstream
persistence-layer implementations **must** use these mappings.

The authoritative source is `Neo4jMapping.cs` in the
`CodeIntelligence.GraphMemory.Schema` namespace. This document mirrors
that code for quick reference.

## Node Labels

Neo4j labels use **PascalCase**.

| `NodeKind` (C# enum) | JSON wire value | Neo4j Label |
|---|---|---|
| `Repo` | `"repo"` | `:Repo` |
| `Module` | `"module"` | `:Module` |
| `File` | `"file"` | `:File` |
| `Class` | `"class"` | `:Class` |
| `Function` | `"function"` | `:Function` |
| `Variable` | `"variable"` | `:Variable` |
| `Unknown` | `"unknown"` | `:Unknown` |

Every node also carries the base label `:GraphNode` in addition to its
kind-specific label, enabling queries like `MATCH (n:GraphNode)`.

## Relationship Types

Neo4j relationship types use **UPPER_SNAKE_CASE**.

| `EdgeKind` (C# enum) | JSON wire value | Neo4j Relationship Type |
|---|---|---|
| `Contains` | `"contains"` | `CONTAINS` |
| `Calls` | `"calls"` | `CALLS` |
| `Imports` | `"imports"` | `IMPORTS` |
| `Inherits` | `"inherits"` | `INHERITS` |
| `Expertise` | `"expertise"` | `EXPERTISE` |
| `SimilarTo` | `"similarTo"` | `SIMILAR_TO` |
| `Unknown` | `"unknown"` | `UNKNOWN` |

## Property Mapping

| Graph Schema Field | Neo4j Property | Notes |
|---|---|---|
| `GraphNode.Id` | `id` (unique constraint) | Canonical ID string |
| `GraphNode.Name` | `name` | |
| `GraphNode.QualifiedName` | `qualifiedName` | Null → absent |
| `GraphNode.Language` | `language` | Null → absent |
| `GraphNode.CreatedAt` | `createdAt` | ISO 8601 string |
| `GraphNode.UpdatedAt` | `updatedAt` | ISO 8601 string |
| `GraphNode.Location.*` | `filePath`, `startLine`, etc. | Flattened onto node |
| `GraphNode.Properties.*` | Prefixed `prop_*` | Avoids collisions |
| `GraphEdge.Id` | `id` (relationship property) | |
| `GraphEdge.Weight` | `weight` | Null → absent |
| `GraphEdge.CreatedAt` | `createdAt` | |
| `GraphEdge.Properties.*` | Prefixed `prop_*` | |

## Example Cypher

```cypher
// Create a Repo node
CREATE (n:GraphNode:Repo {
  id: 'repo:my-service',
  name: 'my-service',
  createdAt: '2025-01-01T00:00:00Z',
  updatedAt: '2025-01-01T00:00:00Z'
})

// Create a CONTAINS relationship
MATCH (parent:Repo {id: 'repo:my-service'})
MATCH (child:File {id: 'file:my-service:src/Foo.cs'})
CREATE (parent)-[:CONTAINS {id: 'e1', createdAt: '2025-01-01T00:00:00Z'}]->(child)

// Query all functions called by a class
MATCH (c:Class)-[:CALLS]->(f:Function)
RETURN c.name, f.name, f.qualifiedName
```

## Constraints & Indexes

Recommended Neo4j constraints for the persistence layer:

```cypher
CREATE CONSTRAINT graph_node_id IF NOT EXISTS
FOR (n:GraphNode) REQUIRE n.id IS UNIQUE;

CREATE INDEX graph_node_kind IF NOT EXISTS
FOR (n:GraphNode) ON (n.kind);

CREATE INDEX graph_node_name IF NOT EXISTS
FOR (n:GraphNode) ON (n.name);
```

## Endpoint Type Constraints

The graph validator enforces pairwise endpoint constraints for structural
edge kinds. The Neo4j persistence layer should enforce these constraints
on write operations. See `GraphValidator.EndpointRules` for the
authoritative source, and `VERSIONING.md` for the full table.
