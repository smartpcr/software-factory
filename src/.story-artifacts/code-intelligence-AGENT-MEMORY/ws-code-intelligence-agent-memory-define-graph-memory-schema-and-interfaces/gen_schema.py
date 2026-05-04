import json
import os

def ref(name):
    return {"$ref": f"#/$defs/{name}"}

def extensions_schema():
    """Extension properties with restricted keys: must match ^x- pattern."""
    return {
        "type": "object",
        "propertyNames": {"pattern": "^x-"},
        "additionalProperties": True,
    }

def source_loc_props():
    return {
        "filePath": {"type": "string", "minLength": 1},
        "startLine": {"type": "integer", "minimum": 1},
        "endLine": {"type": "integer", "minimum": 1},
        "startColumn": {"type": "integer", "minimum": 0},
        "endColumn": {"type": "integer", "minimum": 0}
    }

def make_node(kind_const, props_schema):
    return {
        "type": "object",
        "required": ["id", "kind", "name", "properties"],
        "additionalProperties": False,
        "properties": {
            "id": {"type": "string", "minLength": 1},
            "kind": {"type": "string", "const": kind_const},
            "name": {"type": "string"},
            "properties": props_schema,
            "qualifiedName": {"type": "string"},
            "repoId": {"type": "string"},
            "provenance": ref("Provenance"),
            "version": {"type": "integer", "minimum": 0},
            "layer": ref("Layer"),
            "createdAt": {"type": "string", "format": "date-time"},
            "updatedAt": {"type": "string", "format": "date-time"},
            "versionRange": ref("VersionRange"),
            "snapshotId": {"type": "string"},
            "stale": {"type": "boolean"},
            "contentHash": {"type": "string"},
            "embedding": {"type": "array", "items": {"type": "number"}},
            "extensions": extensions_schema()
        }
    }

def make_edge(kind_const, props_schema):
    return {
        "type": "object",
        "required": ["id", "kind", "source", "target", "properties"],
        "additionalProperties": False,
        "properties": {
            "id": {"type": "string", "minLength": 1},
            "kind": {"type": "string", "const": kind_const},
            "source": {"type": "string", "minLength": 1},
            "target": {"type": "string", "minLength": 1},
            "properties": props_schema,
            "provenance": ref("Provenance"),
            "version": {"type": "integer", "minimum": 0},
            "layer": ref("Layer"),
            "createdAt": {"type": "string", "format": "date-time"},
            "updatedAt": {"type": "string", "format": "date-time"},
            "versionRange": ref("VersionRange"),
            "snapshotId": {"type": "string"},
            "stale": {"type": "boolean"},
            "sourceRepoId": {"type": "string"},
            "targetRepoId": {"type": "string"},
            "extensions": extensions_schema()
        }
    }

def props_obj(required, additional, **fields):
    s = {"type": "object", "additionalProperties": additional}
    if required:
        s["required"] = required
    s["properties"] = fields
    return s

sl = source_loc_props()

node_kinds = {
    "repo": props_obj(["languages"], False,
        url={"type": "string"}, defaultBranch={"type": "string"},
        languages={"type": "array", "items": {"type": "string"}}),
    "package": props_obj(["packageManager"], False,
        packageManager={"type": "string"}, version={"type": "string"}, registryUrl={"type": "string"}),
    "module": props_obj(["path"], False,
        path={"type": "string", "minLength": 1}, packageName={"type": "string"}),
    "directory": props_obj(["path"], False,
        path={"type": "string", "minLength": 1}),
    "file": props_obj(["path"], False,
        path={"type": "string", "minLength": 1}, language={"type": "string"},
        loc={"type": "integer", "minimum": 0}, hash={"type": "string"}),
    "class": props_obj(["filePath", "startLine", "endLine"], False,
        **sl, isAbstract={"type": "boolean"}, isExported={"type": "boolean"}),
    "interface": props_obj(["filePath", "startLine", "endLine"], False,
        **sl, isExported={"type": "boolean"}),
    "type_alias": props_obj(["filePath", "startLine", "endLine"], False,
        **sl, typeExpression={"type": "string"}, isExported={"type": "boolean"}),
    "function": props_obj(["filePath", "startLine", "endLine", "parameters"], False,
        **sl, isAsync={"type": "boolean"}, isGenerator={"type": "boolean"}, isExported={"type": "boolean"},
        parameters={"type": "array", "items": ref("FunctionParameter")}, returnType={"type": "string"}),
    "method": props_obj(["filePath", "startLine", "endLine", "parameters"], False,
        **sl, isAsync={"type": "boolean"}, isStatic={"type": "boolean"}, isExported={"type": "boolean"},
        visibility={"type": "string", "enum": ["public", "protected", "private"]},
        parameters={"type": "array", "items": ref("FunctionParameter")}, returnType={"type": "string"}),
    "variable": props_obj(["filePath", "startLine", "endLine", "variableKind"], False,
        **sl, variableKind={"type": "string", "enum": ["const", "let", "var", "property"]},
        isExported={"type": "boolean"}, typeAnnotation={"type": "string"}),
    "enum": props_obj(["filePath", "startLine", "endLine"], False,
        **sl, isExported={"type": "boolean"}, members={"type": "array", "items": {"type": "string"}}),
    "symbol": props_obj(["filePath", "startLine", "endLine", "symbolKind"], False,
        **sl, symbolKind=ref("SymbolKind"), isExported={"type": "boolean"}),
    "semantic_cluster": props_obj(None, False,
        label={"type": "string"}, centroid={"type": "array", "items": {"type": "number"}},
        memberCount={"type": "integer", "minimum": 0}),
    "topic": props_obj(None, False,
        description={"type": "string"}, keywords={"type": "array", "items": {"type": "string"}}),
    "expertise_note": props_obj(["content"], False,
        content={"type": "string", "minLength": 1}, author={"type": "string"},
        createdAt={"type": "string", "format": "date-time"},
        tags={"type": "array", "items": {"type": "string"}}),
    "pattern": props_obj(["content"], False,
        content={"type": "string", "minLength": 1},
        confidence={"type": "number", "minimum": 0, "maximum": 1},
        tags={"type": "array", "items": {"type": "string"}}),
    "pitfall": props_obj(["content"], False,
        content={"type": "string", "minLength": 1},
        severity={"type": "string", "enum": ["low", "medium", "high"]},
        tags={"type": "array", "items": {"type": "string"}}),
    "preference": props_obj(["content"], False,
        content={"type": "string", "minLength": 1}, author={"type": "string"},
        tags={"type": "array", "items": {"type": "string"}}),
    "insight": props_obj(["content"], False,
        content={"type": "string", "minLength": 1},
        confidence={"type": "number", "minimum": 0, "maximum": 1},
        source={"type": "string"}, tags={"type": "array", "items": {"type": "string"}}),
    "workspace": props_obj(["rootPath"], False,
        rootPath={"type": "string", "minLength": 1},
        repoIds={"type": "array", "items": {"type": "string"}},
        description={"type": "string"}),
}

edge_kinds = {
    "contains": props_obj(None, False,
        order={"type": "integer", "minimum": 0}),
    "calls": props_obj(None, False,
        callSite=ref("CallSite"), isConditional={"type": "boolean"}, isDynamic={"type": "boolean"},
        isAsync={"type": "boolean"}, confidence={"type": "number", "minimum": 0, "maximum": 1}),
    "called_by": props_obj(None, False,
        callSite=ref("CallSite"), isConditional={"type": "boolean"}, isDynamic={"type": "boolean"},
        isAsync={"type": "boolean"}, confidence={"type": "number", "minimum": 0, "maximum": 1}),
    "imports": props_obj(None, False,
        importedSymbols={"type": "array", "items": ref("ImportedSymbol")},
        isTypeOnly={"type": "boolean"}, isDefault={"type": "boolean"}, isReExport={"type": "boolean"}),
    "re_exports": props_obj(None, False,
        importedSymbols={"type": "array", "items": ref("ImportedSymbol")},
        isTypeOnly={"type": "boolean"}),
    "inherits": props_obj(["mechanism"], False,
        mechanism=ref("InheritanceMechanism")),
    "overrides": props_obj(None, False,
        callsSuper={"type": "boolean"}),
    "implements": props_obj(None, False,
        isPartial={"type": "boolean"}),
    "instantiates": props_obj(None, False,
        typeArguments={"type": "array", "items": {"type": "string"}}),
    "exports": props_obj(None, False,
        exportedSymbols={"type": "array", "items": ref("ImportedSymbol")},
        isDefault={"type": "boolean"}, isTypeOnly={"type": "boolean"}),
    "expertise": props_obj(["weight", "confidence"], False,
        weight={"type": "number", "minimum": 0, "maximum": 1},
        confidence={"type": "number", "minimum": 0, "maximum": 1},
        insight={"type": "string"},
        reinforcementCount={"type": "integer", "minimum": 0},
        relationship={"type": "string"},
        lastUpdated={"type": "string", "format": "date-time"},
        observationCount={"type": "integer", "minimum": 0},
        sourceAgent={"type": "string"},
        evidence={"type": "array", "items": ref("ExpertiseEvidence")}),
    "applies_to": props_obj(None, False,
        scope={"type": "string"}),
    "similar_to": props_obj(["cosine_similarity"], False,
        cosine_similarity={"type": "number", "minimum": 0, "maximum": 1},
        algorithm={"type": "string"}),
    "belongs_to_cluster": props_obj(None, False,
        strength={"type": "number", "minimum": 0, "maximum": 1}),
    "supersedes": props_obj(None, False,
        reason={"type": "string"}),
    "derived_from": props_obj(None, False,
        method={"type": "string"}),
    "frequently_used_with": props_obj(None, False,
        coOccurrenceCount={"type": "integer", "minimum": 0},
        strength={"type": "number", "minimum": 0, "maximum": 1},
        window={"type": "string"}),
    "depends_on": props_obj(None, False,
        dependencyType={"type": "string"},
        versionConstraint={"type": "string"},
        isDev={"type": "boolean"}),
    "references": props_obj(None, False,
        referenceKind={"type": "string"},
        isWeak={"type": "boolean"}),
}

schema = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "$id": "https://code-intelligence.dev/schemas/graph-memory/0.3.0",
    "title": "CodeGraph",
    "description": "Code knowledge graph schema v0.3.0",
    "type": "object",
    "required": ["schemaVersion", "id", "nodes", "edges", "metadata"],
    "additionalProperties": False,
    "properties": {
        "schemaVersion": {"type": "string", "const": "0.3.0"},
        "id": {"type": "string", "minLength": 1},
        "nodes": {"type": "array", "items": ref("GraphNode")},
        "edges": {"type": "array", "items": ref("GraphEdge")},
        "metadata": ref("GraphMetadata")
    },
    "$defs": {
        "GraphMetadata": {
            "type": "object",
            "required": ["createdAt", "updatedAt"],
            "additionalProperties": False,
            "properties": {
                "createdAt": {"type": "string", "format": "date-time"},
                "updatedAt": {"type": "string", "format": "date-time"},
                "createdBy": {"type": "string"},
                "description": {"type": "string"},
                "commitSha": {"type": "string"},
                "repositoryRoot": {"type": "string"},
                "branch": {"type": "string"},
                "extensions": extensions_schema()
            }
        },
        "Provenance": {
            "type": "object",
            "additionalProperties": False,
            "properties": {
                "analyzer": {"type": "string"},
                "analyzerVersion": {"type": "string"},
                "analyzedAt": {"type": "string", "format": "date-time"},
                "commitSha": {"type": "string"}
            }
        },
        "VersionRange": {
            "type": "object",
            "required": ["introducedIn", "lastSeenIn"],
            "additionalProperties": False,
            "properties": {
                "introducedIn": {"type": "string"},
                "lastSeenIn": {"type": "string"},
                "removedIn": {"type": "string"}
            }
        },
        "Layer": {
            "type": "string",
            "enum": ["structural", "call_chain", "semantic", "expertise"]
        },
        "SourceLocation": {
            "type": "object",
            "required": ["filePath", "startLine", "endLine"],
            "additionalProperties": False,
            "properties": {
                "filePath": {"type": "string", "minLength": 1},
                "startLine": {"type": "integer", "minimum": 1},
                "endLine": {"type": "integer", "minimum": 1},
                "startColumn": {"type": "integer", "minimum": 0},
                "endColumn": {"type": "integer", "minimum": 0}
            }
        },
        "FunctionParameter": {
            "type": "object",
            "required": ["name"],
            "additionalProperties": False,
            "properties": {
                "name": {"type": "string"},
                "typeAnnotation": {"type": "string"},
                "isOptional": {"type": "boolean"},
                "defaultValue": {"type": "string"}
            }
        },
        "SymbolKind": {
            "type": "string",
            "enum": ["enum", "enum_member", "constant", "namespace", "decorator", "other"]
        },
        "CallSite": {
            "type": "object",
            "required": ["line"],
            "additionalProperties": False,
            "properties": {
                "line": {"type": "integer", "minimum": 1},
                "column": {"type": "integer", "minimum": 0}
            }
        },
        "ImportedSymbol": {
            "type": "object",
            "required": ["name"],
            "additionalProperties": False,
            "properties": {
                "name": {"type": "string", "minLength": 1},
                "alias": {"type": "string"}
            }
        },
        "ExpertiseEvidence": {
            "type": "object",
            "required": ["source"],
            "additionalProperties": False,
            "properties": {
                "source": {"type": "string", "minLength": 1},
                "description": {"type": "string"},
                "timestamp": {"type": "string", "format": "date-time"},
                "confidence": {"type": "number", "minimum": 0, "maximum": 1}
            }
        },
        "InheritanceMechanism": {
            "type": "string",
            "enum": ["extends", "implements", "mixin"]
        },
        "GraphNode": {
            "oneOf": [make_node(k, v) for k, v in node_kinds.items()]
        },
        "GraphEdge": {
            "oneOf": [make_edge(k, v) for k, v in edge_kinds.items()]
        }
    }
}

out = json.dumps(schema, indent=2)
script_dir = os.path.dirname(os.path.abspath(__file__))
path = os.path.join(script_dir, "json-schema", "graph-memory.schema.json")
with open(path, "w", encoding="utf-8") as f:
    f.write(out + "\n")
print(f"Written {len(out)} chars to {path}")
