using GraphMemory.Models;

namespace GraphMemory.Tests;

/// <summary>
/// Builds a realistic >=50-node code graph fixture for testing.
/// Represents a small TypeScript repository with structural, call-chain,
/// semantic, and expertise layers.
/// </summary>
public static class FixtureBuilder
{
    public static CodeGraph Build()
    {
        var now = "2026-05-04T00:00:00Z";
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        int edgeSeq = 0;
        string NextEdgeId() => $"e-{++edgeSeq:D3}";

        // --- Repo ---
        nodes.Add(MakeNode("repo-1", NodeKind.Repo, "my-app",
            new RepoProperties { Languages = ["typescript", "css"], Url = "https://github.com/org/my-app", DefaultBranch = "main" },
            layer: Layer.Structural));

        // --- Directories ---
        string[] dirs = ["src", "src/auth", "src/api", "src/models", "src/utils", "tests"];
        foreach (var d in dirs)
        {
            nodes.Add(MakeNode($"dir-{d.Replace("/", "-")}", NodeKind.Directory, d,
                new DirectoryProperties { Path = d }, layer: Layer.Structural));
        }

        // repo -> directories
        foreach (var d in new[] { "src", "tests" })
            edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "repo-1", $"dir-{d}", new ContainsProperties()));

        // nested directories
        foreach (var sub in new[] { "auth", "api", "models", "utils" })
            edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "dir-src", $"dir-src-{sub}", new ContainsProperties()));

        // --- Files ---
        (string id, string dir, string name, string lang, int loc)[] files = [
            ("file-index", "dir-src", "index.ts", "typescript", 50),
            ("file-auth-service", "dir-src-auth", "auth.service.ts", "typescript", 200),
            ("file-auth-guard", "dir-src-auth", "auth.guard.ts", "typescript", 80),
            ("file-user-controller", "dir-src-api", "user.controller.ts", "typescript", 150),
            ("file-product-controller", "dir-src-api", "product.controller.ts", "typescript", 120),
            ("file-user-model", "dir-src-models", "user.model.ts", "typescript", 90),
            ("file-product-model", "dir-src-models", "product.model.ts", "typescript", 75),
            ("file-logger", "dir-src-utils", "logger.ts", "typescript", 40),
            ("file-config", "dir-src-utils", "config.ts", "typescript", 30),
            ("file-hash", "dir-src-utils", "hash.ts", "typescript", 25),
        ];

        foreach (var (id, dir, name, lang, loc) in files)
        {
            nodes.Add(MakeNode(id, NodeKind.File, name,
                new FileProperties { Path = $"{dir.Replace("dir-", "").Replace("-", "/")}/{name}", Language = lang, Loc = loc },
                layer: Layer.Structural));
            edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, dir, id, new ContainsProperties()));
        }

        // --- Module ---
        nodes.Add(MakeNode("mod-auth", NodeKind.Module, "auth",
            new ModuleProperties { Path = "src/auth" }, layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "dir-src-auth", "mod-auth", new ContainsProperties()));

        // --- Package ---
        nodes.Add(MakeNode("pkg-express", NodeKind.Package, "express",
            new PackageProperties { PackageManager = "npm", Version = "4.18.2" }, layer: Layer.Structural));

        // --- Classes ---
        nodes.Add(MakeNode("cls-auth-service", NodeKind.Class, "AuthService",
            new ClassProperties { FilePath = "src/auth/auth.service.ts", StartLine = 10, EndLine = 100, IsExported = true },
            layer: Layer.Structural));
        nodes.Add(MakeNode("cls-user-controller", NodeKind.Class, "UserController",
            new ClassProperties { FilePath = "src/api/user.controller.ts", StartLine = 5, EndLine = 80, IsExported = true },
            layer: Layer.Structural));

        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-auth-service", "cls-auth-service", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-user-controller", "cls-user-controller", new ContainsProperties()));

        // --- Interface ---
        nodes.Add(MakeNode("iface-auth-provider", NodeKind.Interface, "IAuthProvider",
            new InterfaceProperties { FilePath = "src/auth/auth.service.ts", StartLine = 1, EndLine = 8, IsExported = true },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Implements, "cls-auth-service", "iface-auth-provider",
            new ImplementsProperties()));

        // --- TypeAlias ---
        nodes.Add(MakeNode("ta-user-role", NodeKind.TypeAlias, "UserRole",
            new TypeAliasProperties { FilePath = "src/models/user.model.ts", StartLine = 1, EndLine = 1, TypeExpression = "'admin' | 'user' | 'guest'", IsExported = true },
            layer: Layer.Structural));

        // --- Functions ---
        nodes.Add(MakeNode("fn-hash-password", NodeKind.Function, "hashPassword",
            new FunctionProperties
            {
                FilePath = "src/utils/hash.ts", StartLine = 5, EndLine = 15,
                Parameters = [new FunctionParameter { Name = "plain", TypeAnnotation = "string" }],
                ReturnType = "Promise<string>", IsAsync = true, IsExported = true,
            },
            layer: Layer.Structural));
        nodes.Add(MakeNode("fn-verify-password", NodeKind.Function, "verifyPassword",
            new FunctionProperties
            {
                FilePath = "src/utils/hash.ts", StartLine = 17, EndLine = 25,
                Parameters =
                [
                    new FunctionParameter { Name = "plain", TypeAnnotation = "string" },
                    new FunctionParameter { Name = "hash", TypeAnnotation = "string" },
                ],
                ReturnType = "Promise<boolean>", IsAsync = true, IsExported = true,
            },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-hash", "fn-hash-password", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-hash", "fn-verify-password", new ContainsProperties()));

        // --- Methods ---
        nodes.Add(MakeNode("meth-login", NodeKind.Method, "login",
            new MethodProperties
            {
                FilePath = "src/auth/auth.service.ts", StartLine = 20, EndLine = 45,
                Parameters =
                [
                    new FunctionParameter { Name = "email", TypeAnnotation = "string" },
                    new FunctionParameter { Name = "password", TypeAnnotation = "string" },
                ],
                ReturnType = "Promise<Token>", IsAsync = true, Visibility = Visibility.Public,
            },
            layer: Layer.Structural));
        nodes.Add(MakeNode("meth-logout", NodeKind.Method, "logout",
            new MethodProperties
            {
                FilePath = "src/auth/auth.service.ts", StartLine = 47, EndLine = 60,
                Parameters = [new FunctionParameter { Name = "token", TypeAnnotation = "string" }],
                Visibility = Visibility.Public,
            },
            layer: Layer.Structural));
        nodes.Add(MakeNode("meth-get-user", NodeKind.Method, "getUser",
            new MethodProperties
            {
                FilePath = "src/api/user.controller.ts", StartLine = 10, EndLine = 30,
                Parameters = [new FunctionParameter { Name = "id", TypeAnnotation = "string" }],
                ReturnType = "Promise<User>", IsAsync = true, Visibility = Visibility.Public,
            },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "cls-auth-service", "meth-login", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "cls-auth-service", "meth-logout", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "cls-user-controller", "meth-get-user", new ContainsProperties()));

        // --- Variables ---
        nodes.Add(MakeNode("var-max-retries", NodeKind.Variable, "MAX_RETRIES",
            new VariableProperties { FilePath = "src/utils/config.ts", StartLine = 3, EndLine = 3, VariableKind = VariableKind.Const, IsExported = true },
            layer: Layer.Structural));
        nodes.Add(MakeNode("var-default-timeout", NodeKind.Variable, "DEFAULT_TIMEOUT",
            new VariableProperties { FilePath = "src/utils/config.ts", StartLine = 4, EndLine = 4, VariableKind = VariableKind.Const, IsExported = true, TypeAnnotation = "number" },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-config", "var-max-retries", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-config", "var-default-timeout", new ContainsProperties()));

        // --- Enum ---
        nodes.Add(MakeNode("enum-log-level", NodeKind.Enum, "LogLevel",
            new EnumProperties { FilePath = "src/utils/logger.ts", StartLine = 1, EndLine = 6, IsExported = true, Members = ["DEBUG", "INFO", "WARN", "ERROR"] },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "file-logger", "enum-log-level", new ContainsProperties()));

        // --- Symbol ---
        nodes.Add(MakeNode("sym-log-decorator", NodeKind.Symbol, "Log",
            new SymbolProperties { FilePath = "src/utils/logger.ts", StartLine = 8, EndLine = 15, SymbolKind = SymbolKind.Decorator, IsExported = true },
            layer: Layer.Structural));

        // --- Additional files and functions to reach >=50 nodes ---
        (string id, string dir, string name, int loc)[] extraFiles = [
            ("file-middleware", "dir-src-auth", "middleware.ts", 60),
            ("file-error-handler", "dir-src-utils", "error-handler.ts", 45),
            ("file-types", "dir-src-models", "types.ts", 30),
            ("file-router", "dir-src-api", "router.ts", 55),
            ("file-db", "dir-src-utils", "db.ts", 70),
            ("file-test-helpers", "dir-tests", "helpers.ts", 40),
        ];
        foreach (var (id, dir, name, loc) in extraFiles)
        {
            nodes.Add(MakeNode(id, NodeKind.File, name,
                new FileProperties { Path = $"{dir.Replace("dir-", "").Replace("-", "/")}/{name}", Language = "typescript", Loc = loc },
                layer: Layer.Structural));
            edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, dir, id, new ContainsProperties()));
        }

        // Additional functions
        nodes.Add(MakeNode("fn-create-logger", NodeKind.Function, "createLogger",
            new FunctionProperties { FilePath = "src/utils/logger.ts", StartLine = 20, EndLine = 35, Parameters = [new FunctionParameter { Name = "level", TypeAnnotation = "LogLevel" }], ReturnType = "Logger", IsExported = true },
            layer: Layer.Structural));
        nodes.Add(MakeNode("fn-handle-error", NodeKind.Function, "handleError",
            new FunctionProperties { FilePath = "src/utils/error-handler.ts", StartLine = 1, EndLine = 20, Parameters = [new FunctionParameter { Name = "err", TypeAnnotation = "Error" }], IsExported = true },
            layer: Layer.Structural));
        nodes.Add(MakeNode("fn-connect-db", NodeKind.Function, "connectDb",
            new FunctionProperties { FilePath = "src/utils/db.ts", StartLine = 5, EndLine = 25, Parameters = [new FunctionParameter { Name = "url", TypeAnnotation = "string" }], ReturnType = "Promise<Connection>", IsAsync = true, IsExported = true },
            layer: Layer.Structural));

        // Additional methods
        nodes.Add(MakeNode("meth-create-user", NodeKind.Method, "createUser",
            new MethodProperties { FilePath = "src/api/user.controller.ts", StartLine = 35, EndLine = 55, Parameters = [new FunctionParameter { Name = "data", TypeAnnotation = "CreateUserDto" }], ReturnType = "Promise<User>", IsAsync = true, Visibility = Visibility.Public },
            layer: Layer.Structural));
        nodes.Add(MakeNode("meth-validate-token", NodeKind.Method, "validateToken",
            new MethodProperties { FilePath = "src/auth/auth.service.ts", StartLine = 62, EndLine = 80, Parameters = [new FunctionParameter { Name = "token", TypeAnnotation = "string" }], ReturnType = "Promise<boolean>", IsAsync = true, Visibility = Visibility.Public },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "cls-user-controller", "meth-create-user", new ContainsProperties()));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "cls-auth-service", "meth-validate-token", new ContainsProperties()));

        // Additional variables
        nodes.Add(MakeNode("var-db-url", NodeKind.Variable, "DB_URL",
            new VariableProperties { FilePath = "src/utils/config.ts", StartLine = 6, EndLine = 6, VariableKind = VariableKind.Const, IsExported = true, TypeAnnotation = "string" },
            layer: Layer.Structural));
        nodes.Add(MakeNode("var-salt-rounds", NodeKind.Variable, "SALT_ROUNDS",
            new VariableProperties { FilePath = "src/utils/hash.ts", StartLine = 1, EndLine = 1, VariableKind = VariableKind.Const, TypeAnnotation = "number" },
            layer: Layer.Structural));

        // --- Calls (call-chain layer) ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Calls, "meth-login", "fn-hash-password",
            new CallsProperties { CallSite = new CallSite { Line = 25 } }, layer: Layer.CallChain));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Calls, "meth-login", "fn-verify-password",
            new CallsProperties { CallSite = new CallSite { Line = 30, Column = 12 } }, layer: Layer.CallChain));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.CalledBy, "fn-hash-password", "meth-login",
            new CalledByProperties { CallSite = new CallSite { Line = 25 } }, layer: Layer.CallChain));

        // --- Imports ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Imports, "file-auth-service", "file-hash",
            new ImportsProperties { ImportedSymbols = [new ImportedSymbol { Name = "hashPassword" }, new ImportedSymbol { Name = "verifyPassword" }] }));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Imports, "file-user-controller", "file-auth-service",
            new ImportsProperties { ImportedSymbols = [new ImportedSymbol { Name = "AuthService" }] }));

        // --- Exports ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Exports, "file-index", "file-auth-service",
            new ExportsProperties { ExportedSymbols = [new ImportedSymbol { Name = "AuthService" }] }));

        // --- Re-exports ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.ReExports, "file-index", "file-hash",
            new ReExportsProperties { ImportedSymbols = [new ImportedSymbol { Name = "hashPassword" }] }));

        // --- Inherits ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Inherits, "cls-auth-service", "iface-auth-provider",
            new InheritsProperties { Mechanism = InheritanceMechanism.Implements }));

        // --- Overrides ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Overrides, "meth-login", "meth-login",
            new OverridesProperties { CallsSuper = true }));

        // --- Instantiates ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Instantiates, "meth-get-user", "cls-auth-service",
            new InstantiatesProperties { TypeArguments = ["Token"] }));

        // --- Semantic layer ---
        nodes.Add(MakeNode("cluster-auth", NodeKind.SemanticCluster, "authentication-cluster",
            new SemanticClusterProperties { Label = "Authentication", MemberCount = 5, Centroid = [0.1, 0.9, 0.3] },
            layer: Layer.Semantic));
        nodes.Add(MakeNode("topic-security", NodeKind.Topic, "security",
            new TopicProperties { Description = "Security-related code", Keywords = ["auth", "hash", "token", "guard"] },
            layer: Layer.Semantic));

        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.BelongsToCluster, "cls-auth-service", "cluster-auth",
            new BelongsToClusterProperties { Strength = 0.95 }, layer: Layer.Semantic));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.SimilarTo, "fn-hash-password", "fn-verify-password",
            new SimilarToProperties { CosineSimilarity = 0.87, Algorithm = "openai-ada-002" }, layer: Layer.Semantic));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.AppliesTo, "topic-security", "cls-auth-service",
            new AppliesToProperties { Scope = "class" }, layer: Layer.Semantic));

        // --- Expertise layer ---
        nodes.Add(MakeNode("note-bcrypt", NodeKind.ExpertiseNote, "bcrypt-usage",
            new ExpertiseNoteProperties { Content = "Always use bcrypt with cost >= 12 for password hashing.", Author = "senior-dev", Tags = ["security", "best-practice"] },
            layer: Layer.Expertise));
        nodes.Add(MakeNode("pattern-guard", NodeKind.Pattern, "guard-pattern",
            new PatternProperties { Content = "Use auth guards as decorators on controller methods.", Confidence = 0.9, Tags = ["auth", "pattern"] },
            layer: Layer.Expertise));
        nodes.Add(MakeNode("pitfall-timing", NodeKind.Pitfall, "timing-attack",
            new PitfallProperties { Content = "String comparison for password checks is vulnerable to timing attacks.", Severity = "high", Tags = ["security"] },
            layer: Layer.Expertise));
        nodes.Add(MakeNode("pref-async", NodeKind.Preference, "prefer-async",
            new PreferenceProperties { Content = "Prefer async/await over raw promises.", Author = "team-lead", Tags = ["style"] },
            layer: Layer.Expertise));
        nodes.Add(MakeNode("insight-perf", NodeKind.Insight, "hash-perf",
            new InsightProperties { Content = "hashPassword is the bottleneck in login flow at p99.", Confidence = 0.75, Source = "profiler" },
            layer: Layer.Expertise));

        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Expertise, "note-bcrypt", "fn-hash-password",
            new ExpertiseProperties { Weight = 0.95, Confidence = 0.9, Insight = "bcrypt cost factor impacts latency", ReinforcementCount = 3, ObservationCount = 10 },
            layer: Layer.Expertise));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.AppliesTo, "pattern-guard", "cls-user-controller",
            new AppliesToProperties { Scope = "class" }, layer: Layer.Expertise));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Supersedes, "insight-perf", "note-bcrypt",
            new SupersedesProperties { Reason = "Updated with profiling data" }, layer: Layer.Expertise));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.DerivedFrom, "insight-perf", "fn-hash-password",
            new DerivedFromProperties { Method = "profiling" }, layer: Layer.Expertise));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.FrequentlyUsedWith, "fn-hash-password", "fn-verify-password",
            new FrequentlyUsedWithProperties { CoOccurrenceCount = 42, Strength = 0.88, Window = "file" },
            layer: Layer.Expertise));

        // --- Workspace (multi-repo) ---
        nodes.Add(MakeNode("ws-root", NodeKind.Workspace, "my-workspace",
            new WorkspaceProperties { RootPath = "/home/dev/workspace", RepoIds = ["repo-1"], Description = "Main development workspace" },
            layer: Layer.Structural));
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Contains, "ws-root", "repo-1", new ContainsProperties()));

        // --- DependsOn ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.DependsOn, "repo-1", "pkg-express",
            new DependsOnProperties { DependencyType = "runtime", VersionConstraint = "^4.18.0" }));

        // --- References ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.References, "meth-get-user", "cls-auth-service",
            new ReferencesProperties { ReferenceKind = "type_usage" }));

        // --- Expertise with evidence and sourceAgent ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Expertise, "pattern-guard", "meth-login",
            new ExpertiseProperties
            {
                Weight = 0.7, Confidence = 0.85,
                SourceAgent = "code-review-bot",
                Evidence =
                [
                    new ExpertiseEvidence { Source = "code-review", Description = "Guard pattern observed in PR #42" },
                    new ExpertiseEvidence { Source = "static-analysis", Description = "Decorator usage detected", Confidence = 0.9 },
                ],
            },
            layer: Layer.Expertise));

        // --- Calls with confidence and isAsync ---
        edges.Add(MakeEdge(NextEdgeId(), EdgeKind.Calls, "meth-get-user", "fn-connect-db",
            new CallsProperties { CallSite = new CallSite { Line = 15 }, IsAsync = true, Confidence = 0.95 },
            layer: Layer.CallChain));

        return new CodeGraph
        {
            SchemaVersion = CodeGraph.CurrentSchemaVersion,
            Id = "fixture-graph-1",
            Nodes = nodes,
            Edges = edges,
            Metadata = new GraphMetadata
            {
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = "test-fixture",
                Description = "Test fixture for graph memory schema validation",
                CommitSha = "abc123def456",
                RepositoryRoot = "/home/dev/my-app",
                Branch = "main",
            },
        };
    }

    private static GraphNode MakeNode(string id, NodeKind kind, string name, INodeProperties properties, Layer? layer = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            Name = name,
            Properties = properties,
            Layer = layer,
            CreatedAt = "2026-05-04T00:00:00Z",
            UpdatedAt = "2026-05-04T00:00:00Z",
        };

    private static GraphEdge MakeEdge(string id, EdgeKind kind, string source, string target, IEdgeProperties properties, Layer? layer = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            Source = source,
            Target = target,
            Properties = properties,
            Layer = layer,
        };
}
