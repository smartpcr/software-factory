/**
 * JSON Schema (draft-07) for the agent-memory knowledge graph.
 *
 * This module is the programmatic source of truth.  A static copy lives at
 * `schema.json` in the package root; the test suite asserts parity between
 * the two so they can never drift.
 */
export declare const graphMemorySchema: {
    type: "object";
    properties: {
        nodes: {
            type: "array";
            items: {
                type: "object";
                properties: {
                    id: {
                        type: "string";
                        minLength: number;
                    };
                    kind: {
                        type: "string";
                        enum: string[];
                    };
                    qualified_name: {
                        type: "string";
                        minLength: number;
                    };
                    layer: {
                        type: "array";
                        items: {
                            type: "string";
                            enum: string[];
                        };
                        minItems: number;
                    };
                    source_location: {
                        type: "object";
                        properties: {
                            file_path: {
                                type: "string";
                                minLength: number;
                            };
                            start_line: {
                                type: "integer";
                                minimum: number;
                            };
                            start_column: {
                                type: "integer";
                                minimum: number;
                            };
                            end_line: {
                                type: "integer";
                                minimum: number;
                            };
                            end_column: {
                                type: "integer";
                                minimum: number;
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                    };
                    content_hash: {
                        type: "string";
                    };
                    summary: {
                        type: "string";
                    };
                    version_range: {
                        type: "object";
                        properties: {
                            since: {
                                type: "string";
                                minLength: number;
                            };
                            until: {
                                type: "string";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                    };
                    last_touched: {
                        type: "string";
                        format: "date-time";
                    };
                    embedding: {
                        type: "array";
                        items: {
                            type: "number";
                        };
                    };
                    metadata: {
                        type: "object";
                    };
                    properties: {
                        type: "object";
                    };
                };
                required: string[];
                additionalProperties: boolean;
                allOf: {
                    if: {
                        properties: {
                            kind: {
                                const: string;
                            };
                        };
                    };
                    then: {
                        properties: {
                            properties: object;
                        };
                    };
                }[];
            };
        };
        edges: {
            type: "array";
            items: {
                type: "object";
                properties: {
                    id: {
                        type: "string";
                        minLength: number;
                    };
                    source: {
                        type: "string";
                        minLength: number;
                    };
                    target: {
                        type: "string";
                        minLength: number;
                    };
                    relation: {
                        type: "string";
                        enum: string[];
                    };
                    layer: {
                        type: "string";
                        enum: string[];
                    };
                    weight: {
                        type: "number";
                        minimum: number;
                        maximum: number;
                    };
                    version_range: {
                        type: "object";
                        properties: {
                            since: {
                                type: "string";
                                minLength: number;
                            };
                            until: {
                                type: "string";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                    };
                    stale: {
                        type: "boolean";
                    };
                    provenance: {
                        type: "object";
                        properties: {
                            agent_session_id: {
                                type: "string";
                            };
                            git_commit: {
                                type: "string";
                            };
                            operation: {
                                type: "string";
                                minLength: number;
                            };
                            timestamp: {
                                type: "string";
                                format: "date-time";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                    };
                    properties: {
                        type: "object";
                    };
                };
                required: string[];
                additionalProperties: boolean;
                allOf: {
                    if: {
                        properties: {
                            relation: {
                                const: string;
                            };
                        };
                    };
                    then: Record<string, unknown>;
                }[];
            };
        };
        snapshot: {
            type: "object";
            properties: {
                id: {
                    type: "string";
                    minLength: number;
                };
                commit_sha: {
                    type: "string";
                    minLength: number;
                };
                parent_sha: {
                    type: "string";
                };
                created_at: {
                    type: "string";
                    format: "date-time";
                };
                stats: {
                    type: "object";
                    properties: {
                        node_count: {
                            type: "integer";
                            minimum: number;
                        };
                        edge_count: {
                            type: "integer";
                            minimum: number;
                        };
                        expertise_node_count: {
                            type: "integer";
                            minimum: number;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
    };
    required: string[];
    additionalProperties: boolean;
    $schema: string;
    $id: string;
    title: string;
    description: string;
    definitions: {
        GraphLayer: {
            type: "string";
            enum: string[];
        };
        NodeKind: {
            type: "string";
            enum: string[];
        };
        EdgeRelation: {
            type: "string";
            enum: string[];
        };
        SourceLocation: {
            type: "object";
            properties: {
                file_path: {
                    type: "string";
                    minLength: number;
                };
                start_line: {
                    type: "integer";
                    minimum: number;
                };
                start_column: {
                    type: "integer";
                    minimum: number;
                };
                end_line: {
                    type: "integer";
                    minimum: number;
                };
                end_column: {
                    type: "integer";
                    minimum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        VersionRange: {
            type: "object";
            properties: {
                since: {
                    type: "string";
                    minLength: number;
                };
                until: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        Provenance: {
            type: "object";
            properties: {
                agent_session_id: {
                    type: "string";
                };
                git_commit: {
                    type: "string";
                };
                operation: {
                    type: "string";
                    minLength: number;
                };
                timestamp: {
                    type: "string";
                    format: "date-time";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        Citation: {
            type: "object";
            properties: {
                node_id: {
                    type: "string";
                    minLength: number;
                };
                commit_sha: {
                    type: "string";
                };
                snippet: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        RepoProperties: {
            type: "object";
            properties: {
                root_path: {
                    type: "string";
                    minLength: number;
                };
                vcs_url: {
                    type: "string";
                };
                default_branch: {
                    type: "string";
                    minLength: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        DirectoryProperties: {
            type: "object";
            properties: {
                path: {
                    type: "string";
                    minLength: number;
                };
                depth: {
                    type: "integer";
                    minimum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        PackageProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                version: {
                    type: "string";
                };
                path: {
                    type: "string";
                    minLength: number;
                };
                manager: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ModuleProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                };
                language: {
                    type: "string";
                    minLength: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        FileProperties: {
            type: "object";
            properties: {
                path: {
                    type: "string";
                    minLength: number;
                };
                language: {
                    type: "string";
                    minLength: number;
                };
                hash: {
                    type: "string";
                    minLength: number;
                };
                line_count: {
                    type: "integer";
                    minimum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ClassProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                is_abstract: {
                    type: "boolean";
                };
                is_exported: {
                    type: "boolean";
                };
                docstring: {
                    type: "string";
                };
                constructor_signature: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        InterfaceProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                is_exported: {
                    type: "boolean";
                };
                docstring: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        EnumProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                is_exported: {
                    type: "boolean";
                };
                members: {
                    type: "array";
                    items: {
                        type: "string";
                    };
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        TypeAliasProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                is_exported: {
                    type: "boolean";
                };
                type_expression: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        FunctionProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                signature: {
                    type: "string";
                    minLength: number;
                };
                is_exported: {
                    type: "boolean";
                };
                is_async: {
                    type: "boolean";
                };
                complexity: {
                    type: "number";
                    minimum: number;
                };
                docstring: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        MethodProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                signature: {
                    type: "string";
                    minLength: number;
                };
                is_static: {
                    type: "boolean";
                };
                is_async: {
                    type: "boolean";
                };
                visibility: {
                    type: "string";
                    enum: string[];
                };
                complexity: {
                    type: "number";
                    minimum: number;
                };
                docstring: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        VariableProperties: {
            type: "object";
            properties: {
                name: {
                    type: "string";
                    minLength: number;
                };
                is_exported: {
                    type: "boolean";
                };
                type_annotation: {
                    type: "string";
                };
                is_const: {
                    type: "boolean";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        SemanticClusterProperties: {
            type: "object";
            properties: {
                label: {
                    type: "string";
                    minLength: number;
                };
                description: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        TopicProperties: {
            type: "object";
            properties: {
                title: {
                    type: "string";
                    minLength: number;
                };
                summary: {
                    type: "string";
                    minLength: number;
                };
                related_terms: {
                    type: "array";
                    items: {
                        type: "string";
                    };
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ExpertiseNodeProperties: {
            type: "object";
            properties: {
                title: {
                    type: "string";
                    minLength: number;
                };
                body: {
                    type: "string";
                    minLength: number;
                };
                confidence: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
                source_agent: {
                    type: "string";
                    minLength: number;
                };
                tags: {
                    type: "array";
                    items: {
                        type: "string";
                    };
                };
                evidence: {
                    type: "array";
                    items: {
                        type: "object";
                        properties: {
                            node_id: {
                                type: "string";
                                minLength: number;
                            };
                            commit_sha: {
                                type: "string";
                            };
                            snippet: {
                                type: "string";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                    };
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ContainsEdgeProperties: {
            type: "object";
            properties: {
                order: {
                    type: "integer";
                    minimum: number;
                };
            };
            additionalProperties: boolean;
        };
        ExportsEdgeProperties: {
            type: "object";
            properties: {
                alias: {
                    type: "string";
                };
                is_default: {
                    type: "boolean";
                };
            };
            additionalProperties: boolean;
        };
        ImportsEdgeProperties: {
            type: "object";
            properties: {
                alias: {
                    type: "string";
                };
                is_wildcard: {
                    type: "boolean";
                };
                is_type_only: {
                    type: "boolean";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ReExportsEdgeProperties: {
            type: "object";
            properties: {
                alias: {
                    type: "string";
                };
                is_wildcard: {
                    type: "boolean";
                };
            };
            additionalProperties: boolean;
        };
        CallsEdgeProperties: {
            type: "object";
            properties: {
                arg_count: {
                    type: "integer";
                    minimum: number;
                };
                is_async: {
                    type: "boolean";
                };
                is_conditional: {
                    type: "boolean";
                };
                call_site: {
                    type: "object";
                    properties: {
                        file_path: {
                            type: "string";
                            minLength: number;
                        };
                        start_line: {
                            type: "integer";
                            minimum: number;
                        };
                        start_column: {
                            type: "integer";
                            minimum: number;
                        };
                        end_line: {
                            type: "integer";
                            minimum: number;
                        };
                        end_column: {
                            type: "integer";
                            minimum: number;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
                resolved: {
                    type: "boolean";
                };
                target_name: {
                    type: "string";
                };
            };
            additionalProperties: boolean;
        };
        CalledByEdgeProperties: {
            type: "object";
            properties: {
                arg_count: {
                    type: "integer";
                    minimum: number;
                };
                call_site: {
                    type: "object";
                    properties: {
                        file_path: {
                            type: "string";
                            minLength: number;
                        };
                        start_line: {
                            type: "integer";
                            minimum: number;
                        };
                        start_column: {
                            type: "integer";
                            minimum: number;
                        };
                        end_line: {
                            type: "integer";
                            minimum: number;
                        };
                        end_column: {
                            type: "integer";
                            minimum: number;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
            };
            additionalProperties: boolean;
        };
        OverridesEdgeProperties: {
            type: "object";
            properties: {
                is_abstract: {
                    type: "boolean";
                };
            };
            additionalProperties: boolean;
        };
        ImplementsEdgeProperties: {
            type: "object";
            properties: {
                partial: {
                    type: "boolean";
                };
            };
            additionalProperties: boolean;
        };
        InstantiatesEdgeProperties: {
            type: "object";
            properties: {
                arg_count: {
                    type: "integer";
                    minimum: number;
                };
            };
            additionalProperties: boolean;
        };
        SimilarToEdgeProperties: {
            type: "object";
            properties: {
                cosine_similarity: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        BelongsToClusterEdgeProperties: {
            type: "object";
            properties: {
                membership_score: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
            };
            additionalProperties: boolean;
        };
        AppliesToEdgeProperties: {
            type: "object";
            properties: {
                confidence: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
                scope: {
                    type: "string";
                    enum: string[];
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        SupersedesEdgeProperties: {
            type: "object";
            properties: {
                reason: {
                    type: "string";
                };
                superseded_at: {
                    type: "string";
                    format: "date-time";
                };
            };
            additionalProperties: boolean;
        };
        DerivedFromEdgeProperties: {
            type: "object";
            properties: {
                derivation_type: {
                    type: "string";
                };
            };
            additionalProperties: boolean;
        };
        ExpertiseEdgeProperties: {
            type: "object";
            properties: {
                confidence: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
                kind: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        FrequentlyUsedWithEdgeProperties: {
            type: "object";
            properties: {
                cooccurrence_count: {
                    type: "integer";
                    minimum: number;
                };
                correlation: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        ReferencesEdgeProperties: {
            type: "object";
            properties: {
                reference_kind: {
                    type: "string";
                };
            };
            additionalProperties: boolean;
        };
        DependsOnEdgeProperties: {
            type: "object";
            properties: {
                is_dev_dependency: {
                    type: "boolean";
                };
                version_constraint: {
                    type: "string";
                };
            };
            additionalProperties: boolean;
        };
        InheritsEdgeProperties: {
            type: "object";
            properties: {
                is_virtual: {
                    type: "boolean";
                };
            };
            additionalProperties: boolean;
        };
        GraphNode: {
            type: "object";
            properties: {
                id: {
                    type: "string";
                    minLength: number;
                };
                kind: {
                    type: "string";
                    enum: string[];
                };
                qualified_name: {
                    type: "string";
                    minLength: number;
                };
                layer: {
                    type: "array";
                    items: {
                        type: "string";
                        enum: string[];
                    };
                    minItems: number;
                };
                source_location: {
                    type: "object";
                    properties: {
                        file_path: {
                            type: "string";
                            minLength: number;
                        };
                        start_line: {
                            type: "integer";
                            minimum: number;
                        };
                        start_column: {
                            type: "integer";
                            minimum: number;
                        };
                        end_line: {
                            type: "integer";
                            minimum: number;
                        };
                        end_column: {
                            type: "integer";
                            minimum: number;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
                content_hash: {
                    type: "string";
                };
                summary: {
                    type: "string";
                };
                version_range: {
                    type: "object";
                    properties: {
                        since: {
                            type: "string";
                            minLength: number;
                        };
                        until: {
                            type: "string";
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
                last_touched: {
                    type: "string";
                    format: "date-time";
                };
                embedding: {
                    type: "array";
                    items: {
                        type: "number";
                    };
                };
                metadata: {
                    type: "object";
                };
                properties: {
                    type: "object";
                };
            };
            required: string[];
            additionalProperties: boolean;
            allOf: {
                if: {
                    properties: {
                        kind: {
                            const: string;
                        };
                    };
                };
                then: {
                    properties: {
                        properties: object;
                    };
                };
            }[];
        };
        GraphEdge: {
            type: "object";
            properties: {
                id: {
                    type: "string";
                    minLength: number;
                };
                source: {
                    type: "string";
                    minLength: number;
                };
                target: {
                    type: "string";
                    minLength: number;
                };
                relation: {
                    type: "string";
                    enum: string[];
                };
                layer: {
                    type: "string";
                    enum: string[];
                };
                weight: {
                    type: "number";
                    minimum: number;
                    maximum: number;
                };
                version_range: {
                    type: "object";
                    properties: {
                        since: {
                            type: "string";
                            minLength: number;
                        };
                        until: {
                            type: "string";
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
                stale: {
                    type: "boolean";
                };
                provenance: {
                    type: "object";
                    properties: {
                        agent_session_id: {
                            type: "string";
                        };
                        git_commit: {
                            type: "string";
                        };
                        operation: {
                            type: "string";
                            minLength: number;
                        };
                        timestamp: {
                            type: "string";
                            format: "date-time";
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
                properties: {
                    type: "object";
                };
            };
            required: string[];
            additionalProperties: boolean;
            allOf: {
                if: {
                    properties: {
                        relation: {
                            const: string;
                        };
                    };
                };
                then: Record<string, unknown>;
            }[];
        };
        GraphStats: {
            type: "object";
            properties: {
                node_count: {
                    type: "integer";
                    minimum: number;
                };
                edge_count: {
                    type: "integer";
                    minimum: number;
                };
                expertise_node_count: {
                    type: "integer";
                    minimum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        Snapshot: {
            type: "object";
            properties: {
                id: {
                    type: "string";
                    minLength: number;
                };
                commit_sha: {
                    type: "string";
                    minLength: number;
                };
                parent_sha: {
                    type: "string";
                };
                created_at: {
                    type: "string";
                    format: "date-time";
                };
                stats: {
                    type: "object";
                    properties: {
                        node_count: {
                            type: "integer";
                            minimum: number;
                        };
                        edge_count: {
                            type: "integer";
                            minimum: number;
                        };
                        expertise_node_count: {
                            type: "integer";
                            minimum: number;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        KnowledgeGraph: {
            type: "object";
            properties: {
                nodes: {
                    type: "array";
                    items: {
                        type: "object";
                        properties: {
                            id: {
                                type: "string";
                                minLength: number;
                            };
                            kind: {
                                type: "string";
                                enum: string[];
                            };
                            qualified_name: {
                                type: "string";
                                minLength: number;
                            };
                            layer: {
                                type: "array";
                                items: {
                                    type: "string";
                                    enum: string[];
                                };
                                minItems: number;
                            };
                            source_location: {
                                type: "object";
                                properties: {
                                    file_path: {
                                        type: "string";
                                        minLength: number;
                                    };
                                    start_line: {
                                        type: "integer";
                                        minimum: number;
                                    };
                                    start_column: {
                                        type: "integer";
                                        minimum: number;
                                    };
                                    end_line: {
                                        type: "integer";
                                        minimum: number;
                                    };
                                    end_column: {
                                        type: "integer";
                                        minimum: number;
                                    };
                                };
                                required: string[];
                                additionalProperties: boolean;
                            };
                            content_hash: {
                                type: "string";
                            };
                            summary: {
                                type: "string";
                            };
                            version_range: {
                                type: "object";
                                properties: {
                                    since: {
                                        type: "string";
                                        minLength: number;
                                    };
                                    until: {
                                        type: "string";
                                    };
                                };
                                required: string[];
                                additionalProperties: boolean;
                            };
                            last_touched: {
                                type: "string";
                                format: "date-time";
                            };
                            embedding: {
                                type: "array";
                                items: {
                                    type: "number";
                                };
                            };
                            metadata: {
                                type: "object";
                            };
                            properties: {
                                type: "object";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                        allOf: {
                            if: {
                                properties: {
                                    kind: {
                                        const: string;
                                    };
                                };
                            };
                            then: {
                                properties: {
                                    properties: object;
                                };
                            };
                        }[];
                    };
                };
                edges: {
                    type: "array";
                    items: {
                        type: "object";
                        properties: {
                            id: {
                                type: "string";
                                minLength: number;
                            };
                            source: {
                                type: "string";
                                minLength: number;
                            };
                            target: {
                                type: "string";
                                minLength: number;
                            };
                            relation: {
                                type: "string";
                                enum: string[];
                            };
                            layer: {
                                type: "string";
                                enum: string[];
                            };
                            weight: {
                                type: "number";
                                minimum: number;
                                maximum: number;
                            };
                            version_range: {
                                type: "object";
                                properties: {
                                    since: {
                                        type: "string";
                                        minLength: number;
                                    };
                                    until: {
                                        type: "string";
                                    };
                                };
                                required: string[];
                                additionalProperties: boolean;
                            };
                            stale: {
                                type: "boolean";
                            };
                            provenance: {
                                type: "object";
                                properties: {
                                    agent_session_id: {
                                        type: "string";
                                    };
                                    git_commit: {
                                        type: "string";
                                    };
                                    operation: {
                                        type: "string";
                                        minLength: number;
                                    };
                                    timestamp: {
                                        type: "string";
                                        format: "date-time";
                                    };
                                };
                                required: string[];
                                additionalProperties: boolean;
                            };
                            properties: {
                                type: "object";
                            };
                        };
                        required: string[];
                        additionalProperties: boolean;
                        allOf: {
                            if: {
                                properties: {
                                    relation: {
                                        const: string;
                                    };
                                };
                            };
                            then: Record<string, unknown>;
                        }[];
                    };
                };
                snapshot: {
                    type: "object";
                    properties: {
                        id: {
                            type: "string";
                            minLength: number;
                        };
                        commit_sha: {
                            type: "string";
                            minLength: number;
                        };
                        parent_sha: {
                            type: "string";
                        };
                        created_at: {
                            type: "string";
                            format: "date-time";
                        };
                        stats: {
                            type: "object";
                            properties: {
                                node_count: {
                                    type: "integer";
                                    minimum: number;
                                };
                                edge_count: {
                                    type: "integer";
                                    minimum: number;
                                };
                                expertise_node_count: {
                                    type: "integer";
                                    minimum: number;
                                };
                            };
                            required: string[];
                            additionalProperties: boolean;
                        };
                    };
                    required: string[];
                    additionalProperties: boolean;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
    };
};
/** The GraphNode sub-schema, for standalone node validation. */
export declare const graphNodeSubSchema: {
    type: "object";
    properties: {
        id: {
            type: "string";
            minLength: number;
        };
        kind: {
            type: "string";
            enum: string[];
        };
        qualified_name: {
            type: "string";
            minLength: number;
        };
        layer: {
            type: "array";
            items: {
                type: "string";
                enum: string[];
            };
            minItems: number;
        };
        source_location: {
            type: "object";
            properties: {
                file_path: {
                    type: "string";
                    minLength: number;
                };
                start_line: {
                    type: "integer";
                    minimum: number;
                };
                start_column: {
                    type: "integer";
                    minimum: number;
                };
                end_line: {
                    type: "integer";
                    minimum: number;
                };
                end_column: {
                    type: "integer";
                    minimum: number;
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        content_hash: {
            type: "string";
        };
        summary: {
            type: "string";
        };
        version_range: {
            type: "object";
            properties: {
                since: {
                    type: "string";
                    minLength: number;
                };
                until: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        last_touched: {
            type: "string";
            format: "date-time";
        };
        embedding: {
            type: "array";
            items: {
                type: "number";
            };
        };
        metadata: {
            type: "object";
        };
        properties: {
            type: "object";
        };
    };
    required: string[];
    additionalProperties: boolean;
    allOf: {
        if: {
            properties: {
                kind: {
                    const: string;
                };
            };
        };
        then: {
            properties: {
                properties: object;
            };
        };
    }[];
};
/** The GraphEdge sub-schema, for standalone edge validation. */
export declare const graphEdgeSubSchema: {
    type: "object";
    properties: {
        id: {
            type: "string";
            minLength: number;
        };
        source: {
            type: "string";
            minLength: number;
        };
        target: {
            type: "string";
            minLength: number;
        };
        relation: {
            type: "string";
            enum: string[];
        };
        layer: {
            type: "string";
            enum: string[];
        };
        weight: {
            type: "number";
            minimum: number;
            maximum: number;
        };
        version_range: {
            type: "object";
            properties: {
                since: {
                    type: "string";
                    minLength: number;
                };
                until: {
                    type: "string";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        stale: {
            type: "boolean";
        };
        provenance: {
            type: "object";
            properties: {
                agent_session_id: {
                    type: "string";
                };
                git_commit: {
                    type: "string";
                };
                operation: {
                    type: "string";
                    minLength: number;
                };
                timestamp: {
                    type: "string";
                    format: "date-time";
                };
            };
            required: string[];
            additionalProperties: boolean;
        };
        properties: {
            type: "object";
        };
    };
    required: string[];
    additionalProperties: boolean;
    allOf: {
        if: {
            properties: {
                relation: {
                    const: string;
                };
            };
        };
        then: Record<string, unknown>;
    }[];
};
//# sourceMappingURL=schema.d.ts.map