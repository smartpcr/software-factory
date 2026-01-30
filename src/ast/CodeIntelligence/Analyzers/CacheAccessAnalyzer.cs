// -----------------------------------------------------------------------
// <copyright file="CacheAccessAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect cache access patterns (IMemoryCache, IDistributedCache, Redis, etc.).
/// Uses pure AST-based detection without hardcoded type name patterns.
/// </summary>
public sealed class CacheAccessAnalyzer
{
    /// <summary>
    /// Core cache types for AST-based detection.
    /// </summary>
    private static readonly HashSet<string> CacheBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "IMemoryCache",
        "MemoryCache",
        "IDistributedCache",
        "IDatabase",         // StackExchange.Redis
        "IConnectionMultiplexer",
        "ICacheClient",
        "IHybridCache",
        "HybridCache",
    };

    /// <summary>
    /// Cache read methods.
    /// </summary>
    private static readonly HashSet<string> CacheReadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // IMemoryCache
        "Get",
        "TryGetValue",
        "GetOrCreate",
        "GetOrCreateAsync",
        // IDistributedCache
        "GetAsync",
        "GetString",
        "GetStringAsync",
        // Redis IDatabase
        "StringGet",
        "StringGetAsync",
        "HashGet",
        "HashGetAsync",
        "HashGetAll",
        "HashGetAllAsync",
        "ListRange",
        "ListRangeAsync",
        "SetMembers",
        "SetMembersAsync",
        "KeyExists",
        "KeyExistsAsync",
    };

    /// <summary>
    /// Cache write methods.
    /// </summary>
    private static readonly HashSet<string> CacheWriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // IMemoryCache
        "Set",
        "CreateEntry",
        // IDistributedCache
        "SetAsync",
        "SetString",
        "SetStringAsync",
        "Refresh",
        "RefreshAsync",
        // Redis IDatabase
        "StringSet",
        "StringSetAsync",
        "HashSet",
        "HashSetAsync",
        "ListLeftPush",
        "ListLeftPushAsync",
        "ListRightPush",
        "ListRightPushAsync",
        "SetAdd",
        "SetAddAsync",
    };

    /// <summary>
    /// Cache delete methods.
    /// </summary>
    private static readonly HashSet<string> CacheDeleteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // IDistributedCache
        "Remove",
        "RemoveAsync",
        // Redis IDatabase
        "KeyDelete",
        "KeyDeleteAsync",
        "HashDelete",
        "HashDeleteAsync",
        "ListRemove",
        "ListRemoveAsync",
        "SetRemove",
        "SetRemoveAsync",
    };

    /// <summary>
    /// Analyzes a syntax tree for cache access operations.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected cache access operations.</returns>
    public IReadOnlyList<CacheAccessInfo> Analyze(SyntaxTree tree, string filePath)
    {
        return this.Analyze(tree, filePath, null);
    }

    /// <summary>
    /// Analyzes a syntax tree for cache access operations with wrapper chain support.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <param name="cacheWrapperTypes">Optional set of cache wrapper types discovered via AST.</param>
    /// <returns>List of detected cache access operations.</returns>
    public IReadOnlyList<CacheAccessInfo> Analyze(SyntaxTree tree, string filePath, HashSet<string>? cacheWrapperTypes)
    {
        var accesses = new List<CacheAccessInfo>();
        var root = tree.GetRoot();

        // Find all invocations
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var accessInfo = this.AnalyzeInvocation(invocation, filePath, cacheWrapperTypes);
            if (accessInfo != null)
            {
                accesses.Add(accessInfo);
            }
        }

        return accesses;
    }

    /// <summary>
    /// Builds a cache wrapper chain by tracing constructor dependencies.
    /// Similar to HTTP client wrapper chain detection.
    /// </summary>
    /// <param name="trees">Collection of syntax trees to analyze.</param>
    /// <returns>Set of type names that are cache wrappers.</returns>
    public HashSet<string> BuildCacheWrapperChain(IEnumerable<SyntaxTree> trees)
    {
        // Map: className -> set of constructor parameter types
        var constructorDependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Map: className -> set of field/property types
        var fieldTypes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Map: className -> set of interfaces it implements
        var classInterfaces = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // First pass: collect constructor parameters, field types, and interfaces for all classes
        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classes)
            {
                var className = classDecl.Identifier.Text;

                // Collect constructor parameter types
                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    foreach (var param in ctor.ParameterList.Parameters)
                    {
                        var paramType = param.Type?.ToString();
                        if (paramType != null)
                        {
                            var simpleType = paramType.Split('<')[0].Split('.').Last();
                            if (!constructorDependencies.TryGetValue(className, out var deps))
                            {
                                deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                constructorDependencies[className] = deps;
                            }

                            deps.Add(simpleType);
                        }
                    }
                }

                // Collect field types
                foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
                {
                    var fieldType = field.Declaration.Type.ToString();
                    var simpleType = fieldType.Split('<')[0].Split('.').Last();
                    if (!fieldTypes.TryGetValue(className, out var types))
                    {
                        types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        fieldTypes[className] = types;
                    }

                    types.Add(simpleType);
                }

                // Collect interfaces this class implements
                if (classDecl.BaseList != null)
                {
                    foreach (var baseType in classDecl.BaseList.Types)
                    {
                        var typeName = baseType.Type.ToString();
                        var simpleType = typeName.Split('<')[0].Split('.').Last();

                        // Interfaces typically start with 'I' followed by uppercase
                        if (simpleType.Length > 1 && simpleType.StartsWith("I") && char.IsUpper(simpleType[1]))
                        {
                            if (!classInterfaces.TryGetValue(className, out var interfaces))
                            {
                                interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                classInterfaces[className] = interfaces;
                            }

                            interfaces.Add(simpleType);
                        }
                    }
                }
            }
        }

        // Second pass: find all types that directly or transitively depend on cache types
        var cacheWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Seed with core cache types
        foreach (var baseType in CacheBaseTypes)
        {
            cacheWrappers.Add(baseType);
        }

        // Helper function to add a class and all its interfaces to the wrapper chain
        void AddToWrapperChain(string className)
        {
            cacheWrappers.Add(className);

            // Also add all interfaces this class implements
            if (classInterfaces.TryGetValue(className, out var interfaces))
            {
                foreach (var iface in interfaces)
                {
                    cacheWrappers.Add(iface);
                }
            }
        }

        // Iteratively find wrappers until no more are found
        bool changed;
        do
        {
            changed = false;
            foreach (var (className, deps) in constructorDependencies)
            {
                if (cacheWrappers.Contains(className))
                {
                    continue;
                }

                // Check if any constructor dependency is a cache wrapper
                foreach (var dep in deps)
                {
                    if (cacheWrappers.Contains(dep))
                    {
                        AddToWrapperChain(className);
                        changed = true;
                        break;
                    }
                }
            }

            // Also check field types
            foreach (var (className, types) in fieldTypes)
            {
                if (cacheWrappers.Contains(className))
                {
                    continue;
                }

                foreach (var fieldType in types)
                {
                    if (cacheWrappers.Contains(fieldType))
                    {
                        AddToWrapperChain(className);
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        return cacheWrappers;
    }

    private CacheAccessInfo? AnalyzeInvocation(
        InvocationExpressionSyntax invocation,
        string filePath,
        HashSet<string>? cacheWrapperTypes)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();
        var simpleReceiver = receiver.Split('.').Last();

        // Determine operation type
        CacheOperationType? operationType = null;
        if (CacheReadMethods.Contains(methodName))
        {
            operationType = CacheOperationType.Read;
        }
        else if (CacheWriteMethods.Contains(methodName))
        {
            operationType = CacheOperationType.Write;
        }
        else if (CacheDeleteMethods.Contains(methodName))
        {
            operationType = CacheOperationType.Delete;
        }

        if (operationType == null)
        {
            return null;
        }

        // Determine cache type
        CacheType? cacheType = this.InferCacheType(memberAccess, cacheWrapperTypes);
        if (cacheType == null)
        {
            return null;
        }

        // Extract cache key
        var cacheKey = this.ExtractCacheKey(invocation);

        // Extract value type for write operations
        string? valueType = null;
        if (operationType == CacheOperationType.Write)
        {
            valueType = this.ExtractValueType(invocation);
        }

        // Get containing class and method
        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new CacheAccessInfo
        {
            CacheType = cacheType.Value,
            OperationType = operationType.Value,
            MethodName = methodName,
            ReceiverExpression = receiver,
            CacheKey = cacheKey,
            ValueType = valueType,
            IsAsync = methodName.EndsWith("Async"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = this.CalculateConfidence(cacheType.Value, cacheWrapperTypes != null),
        };
    }

    private CacheType? InferCacheType(MemberAccessExpressionSyntax memberAccess, HashSet<string>? cacheWrapperTypes)
    {
        var receiver = memberAccess.Expression.ToString();
        var simpleReceiver = receiver.Split('.').Last();

        // Check for known cache type patterns in receiver name
        if (receiver.Contains("_memoryCache") || receiver.Contains("_cache") && receiver.Contains("Memory"))
        {
            return CacheType.MemoryCache;
        }

        if (receiver.Contains("_distributedCache") || receiver.Contains("Distributed"))
        {
            return CacheType.DistributedCache;
        }

        if (receiver.Contains("_database") || receiver.Contains("_redis") || receiver.Contains("_db"))
        {
            return CacheType.Redis;
        }

        if (receiver.Contains("_hybridCache"))
        {
            return CacheType.HybridCache;
        }

        // Check method name patterns for Redis
        var methodName = memberAccess.Name.Identifier.Text;
        if (methodName.StartsWith("String") || methodName.StartsWith("Hash") ||
            methodName.StartsWith("List") || methodName.StartsWith("Set") ||
            methodName.StartsWith("Key"))
        {
            return CacheType.Redis;
        }

        // Check against wrapper chain
        if (cacheWrapperTypes != null && cacheWrapperTypes.Contains(simpleReceiver))
        {
            return CacheType.CustomWrapper;
        }

        // Fall back to generic cache if method matches cache patterns
        if (CacheReadMethods.Contains(methodName) || CacheWriteMethods.Contains(methodName) ||
            CacheDeleteMethods.Contains(methodName))
        {
            // Only report if receiver looks like a cache field
            if (receiver.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                receiver.Contains("Cache", StringComparison.Ordinal))
            {
                return CacheType.Unknown;
            }
        }

        return null;
    }

    private string? ExtractCacheKey(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return null;
        }

        var firstArg = args[0].Expression;

        // String literal key
        if (firstArg is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        // Interpolated string
        if (firstArg is InterpolatedStringExpressionSyntax interpolated)
        {
            return interpolated.ToString();
        }

        // Variable or expression
        return firstArg.ToString();
    }

    private string? ExtractValueType(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;

        // For Set operations, value is typically the second argument
        if (args.Count < 2)
        {
            return null;
        }

        var valueArg = args[1].Expression;

        if (valueArg is ObjectCreationExpressionSyntax objectCreation)
        {
            return objectCreation.Type.ToString();
        }

        if (valueArg is IdentifierNameSyntax identifier)
        {
            return identifier.Identifier.Text;
        }

        // Check for generic type in method name (e.g., GetOrCreate<T>)
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName)
        {
            var typeArg = genericName.TypeArgumentList.Arguments.FirstOrDefault();
            if (typeArg != null)
            {
                return typeArg.ToString();
            }
        }

        return null;
    }

    private float CalculateConfidence(CacheType cacheType, bool hasWrapperChain)
    {
        var baseConfidence = cacheType switch
        {
            CacheType.MemoryCache => 0.90f,
            CacheType.DistributedCache => 0.90f,
            CacheType.Redis => 0.85f,
            CacheType.HybridCache => 0.90f,
            CacheType.CustomWrapper => 0.80f,
            _ => 0.70f,
        };

        // Boost confidence if we have wrapper chain analysis
        if (hasWrapperChain)
        {
            baseConfidence = Math.Min(1.0f, baseConfidence + 0.05f);
        }

        return baseConfidence;
    }
}

/// <summary>
/// Information about a detected cache access operation.
/// </summary>
public record CacheAccessInfo
{
    /// <summary>Gets the cache type.</summary>
    public required CacheType CacheType { get; init; }

    /// <summary>Gets the operation type (Read/Write/Delete).</summary>
    public required CacheOperationType OperationType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public required string ReceiverExpression { get; init; }

    /// <summary>Gets the cache key or key expression.</summary>
    public string? CacheKey { get; init; }

    /// <summary>Gets the value type for write operations.</summary>
    public string? ValueType { get; init; }

    /// <summary>Gets whether the operation is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the containing method name.</summary>
    public string? MethodContext { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the detection confidence.</summary>
    public required float Confidence { get; init; }
}

/// <summary>
/// Types of cache systems.
/// </summary>
public enum CacheType
{
    /// <summary>In-memory cache (IMemoryCache).</summary>
    MemoryCache,

    /// <summary>Distributed cache (IDistributedCache).</summary>
    DistributedCache,

    /// <summary>Redis cache (StackExchange.Redis).</summary>
    Redis,

    /// <summary>Hybrid cache (.NET 9+).</summary>
    HybridCache,

    /// <summary>Custom cache wrapper.</summary>
    CustomWrapper,

    /// <summary>Unknown cache type.</summary>
    Unknown,
}

/// <summary>
/// Types of cache operations.
/// </summary>
public enum CacheOperationType
{
    /// <summary>Read operation (Get, TryGetValue).</summary>
    Read,

    /// <summary>Write operation (Set, Add).</summary>
    Write,

    /// <summary>Delete operation (Remove, Delete).</summary>
    Delete,
}
