// -----------------------------------------------------------------------
// <copyright file="DatabaseAccessAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes C# code to detect database access patterns (EF Core, Dapper, ADO.NET).
/// Uses pure AST-based detection.
/// </summary>
public sealed class DatabaseAccessAnalyzer
{
    /// <summary>
    /// DbContext base types for entity detection.
    /// </summary>
    private static readonly HashSet<string> DbContextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DbContext",
        "IdentityDbContext",
    };

    /// <summary>
    /// Entity attributes that mark database entities.
    /// </summary>
    private static readonly HashSet<string> EntityAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Table",
        "Entity",
        "Keyless",
        "Owned",
    };

    /// <summary>
    /// Database query methods.
    /// </summary>
    private static readonly HashSet<string> QueryMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // EF Core LINQ
        "Where",
        "Select",
        "FirstOrDefault",
        "FirstOrDefaultAsync",
        "First",
        "FirstAsync",
        "SingleOrDefault",
        "SingleOrDefaultAsync",
        "Single",
        "SingleAsync",
        "ToList",
        "ToListAsync",
        "ToArray",
        "ToArrayAsync",
        "Any",
        "AnyAsync",
        "All",
        "AllAsync",
        "Count",
        "CountAsync",
        "LongCount",
        "LongCountAsync",
        "Sum",
        "SumAsync",
        "Average",
        "AverageAsync",
        "Min",
        "MinAsync",
        "Max",
        "MaxAsync",
        "Find",
        "FindAsync",
        "Include",
        "ThenInclude",
        "AsNoTracking",
        "AsTracking",
        "AsNoTrackingWithIdentityResolution",
        // Raw SQL
        "FromSqlRaw",
        "FromSqlInterpolated",
        "FromSql",
        // Dapper
        "Query",
        "QueryAsync",
        "QueryFirst",
        "QueryFirstAsync",
        "QueryFirstOrDefault",
        "QueryFirstOrDefaultAsync",
        "QuerySingle",
        "QuerySingleAsync",
        "QuerySingleOrDefault",
        "QuerySingleOrDefaultAsync",
        "QueryMultiple",
        "QueryMultipleAsync",
    };

    /// <summary>
    /// Database mutation methods.
    /// </summary>
    private static readonly HashSet<string> MutationMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        // EF Core
        "Add",
        "AddAsync",
        "AddRange",
        "AddRangeAsync",
        "Update",
        "UpdateRange",
        "Remove",
        "RemoveRange",
        "Attach",
        "AttachRange",
        "Entry",
        "SaveChanges",
        "SaveChangesAsync",
        // EF Core 7+ bulk operations
        "ExecuteUpdate",
        "ExecuteUpdateAsync",
        "ExecuteDelete",
        "ExecuteDeleteAsync",
        // Dapper
        "Execute",
        "ExecuteAsync",
        "ExecuteScalar",
        "ExecuteScalarAsync",
    };

    /// <summary>
    /// Analyzes a syntax tree for database entities.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected database entities.</returns>
    public IReadOnlyList<DatabaseEntityInfo> AnalyzeEntities(SyntaxTree tree, string filePath)
    {
        var entities = new List<DatabaseEntityInfo>();
        var root = tree.GetRoot();

        // Find entities by attribute
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        foreach (var classDecl in classes)
        {
            var entityInfo = this.AnalyzeEntityClass(classDecl, filePath);
            if (entityInfo != null)
            {
                entities.Add(entityInfo);
            }
        }

        // Find entities via DbSet<T> properties in DbContext
        foreach (var classDecl in classes)
        {
            if (!this.IsDbContext(classDecl))
            {
                continue;
            }

            var dbSetEntities = this.ExtractDbSetEntities(classDecl, filePath);
            entities.AddRange(dbSetEntities);
        }

        return entities;
    }

    /// <summary>
    /// Analyzes a syntax tree for database queries.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected database queries.</returns>
    public IReadOnlyList<DatabaseQueryInfo> AnalyzeQueries(SyntaxTree tree, string filePath)
    {
        var queries = new List<DatabaseQueryInfo>();
        var root = tree.GetRoot();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var queryInfo = this.AnalyzeQueryInvocation(invocation, filePath);
            if (queryInfo != null)
            {
                queries.Add(queryInfo);
            }
        }

        return queries;
    }

    /// <summary>
    /// Analyzes a syntax tree for database mutations.
    /// </summary>
    /// <param name="tree">The syntax tree to analyze.</param>
    /// <param name="filePath">The file path for location tracking.</param>
    /// <returns>List of detected database mutations.</returns>
    public IReadOnlyList<DatabaseMutationInfo> AnalyzeMutations(SyntaxTree tree, string filePath)
    {
        var mutations = new List<DatabaseMutationInfo>();
        var root = tree.GetRoot();

        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var mutationInfo = this.AnalyzeMutationInvocation(invocation, filePath);
            if (mutationInfo != null)
            {
                mutations.Add(mutationInfo);
            }
        }

        return mutations;
    }

    private DatabaseEntityInfo? AnalyzeEntityClass(ClassDeclarationSyntax classDecl, string filePath)
    {
        // Check for entity attributes
        var hasEntityAttribute = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => EntityAttributes.Contains(a.Name.ToString().Replace("Attribute", "")));

        if (!hasEntityAttribute)
        {
            return null;
        }

        // Extract table name from [Table] attribute if present
        string? tableName = null;
        var tableAttr = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => a.Name.ToString().Replace("Attribute", "") == "Table");

        if (tableAttr?.ArgumentList?.Arguments.Count > 0)
        {
            var arg = tableAttr.ArgumentList.Arguments[0].Expression;
            if (arg is LiteralExpressionSyntax literal)
            {
                tableName = literal.Token.ValueText;
            }
        }

        // Extract properties
        var properties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => new EntityPropertyInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type.ToString(),
                IsKey = p.AttributeLists.SelectMany(al => al.Attributes)
                    .Any(a => a.Name.ToString().Contains("Key")),
                IsNavigation = this.IsNavigationProperty(p),
            })
            .ToList();

        return new DatabaseEntityInfo
        {
            ClassName = classDecl.Identifier.Text,
            TableName = tableName ?? classDecl.Identifier.Text,
            EntityType = this.DetermineEntityType(classDecl),
            Properties = properties,
            FilePath = filePath,
            LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private bool IsDbContext(ClassDeclarationSyntax classDecl)
    {
        if (classDecl.BaseList == null)
        {
            return false;
        }

        return classDecl.BaseList.Types
            .Any(t => DbContextTypes.Contains(t.Type.ToString().Split('<')[0].Split('.').Last()));
    }

    private IEnumerable<DatabaseEntityInfo> ExtractDbSetEntities(ClassDeclarationSyntax dbContext, string filePath)
    {
        var properties = dbContext.Members.OfType<PropertyDeclarationSyntax>();

        foreach (var prop in properties)
        {
            var typeStr = prop.Type.ToString();
            if (!typeStr.StartsWith("DbSet<"))
            {
                continue;
            }

            // Extract entity type from DbSet<EntityType>
            var entityType = typeStr.Replace("DbSet<", "").TrimEnd('>');

            yield return new DatabaseEntityInfo
            {
                ClassName = entityType,
                TableName = prop.Identifier.Text, // DbSet property name is often the table name
                EntityType = DatabaseEntityType.DbSetEntity,
                Properties = new List<EntityPropertyInfo>(),
                DbContextClass = dbContext.Identifier.Text,
                DbSetPropertyName = prop.Identifier.Text,
                FilePath = filePath,
                LineNumber = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Confidence = 0.90f,
            };
        }
    }

    private DatabaseQueryInfo? AnalyzeQueryInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        string? methodName = null;
        string? receiver = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
            receiver = memberAccess.Expression.ToString();
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier)
        {
            methodName = identifier.Identifier.Text;
        }

        if (methodName == null || !QueryMethods.Contains(methodName))
        {
            return null;
        }

        // Verify it's a database query (not just any LINQ)
        if (!this.IsDatabaseQuery(receiver, methodName))
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new DatabaseQueryInfo
        {
            QueryType = this.DetermineQueryType(methodName),
            MethodName = methodName,
            ReceiverExpression = receiver,
            EntityType = this.ExtractEntityType(invocation),
            IsAsync = methodName.EndsWith("Async"),
            UsesRawSql = methodName.StartsWith("FromSql"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = this.CalculateQueryConfidence(methodName, receiver),
        };
    }

    private DatabaseMutationInfo? AnalyzeMutationInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        string? methodName = null;
        string? receiver = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            methodName = memberAccess.Name.Identifier.Text;
            receiver = memberAccess.Expression.ToString();
        }

        if (methodName == null || !MutationMethods.Contains(methodName))
        {
            return null;
        }

        // Verify it's a database mutation
        if (!this.IsDatabaseMutation(receiver, methodName))
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new DatabaseMutationInfo
        {
            MutationType = this.DetermineMutationType(methodName),
            MethodName = methodName,
            ReceiverExpression = receiver,
            EntityType = this.ExtractEntityType(invocation),
            IsAsync = methodName.EndsWith("Async"),
            IsBulkOperation = methodName.StartsWith("Execute") || methodName.Contains("Range"),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = this.CalculateMutationConfidence(methodName, receiver),
        };
    }

    private bool IsDatabaseQuery(string? receiver, string methodName)
    {
        if (receiver == null)
        {
            return false;
        }

        // Check for DbContext patterns
        if (receiver.Contains("_context") || receiver.Contains("_dbContext") ||
            receiver.Contains("_db") || receiver.Contains("DbContext") ||
            receiver.Contains("Context"))
        {
            return true;
        }

        // Check for Dapper patterns
        if (methodName.StartsWith("Query") && 
            (receiver.Contains("connection") || receiver.Contains("Connection")))
        {
            return true;
        }

        // Check for DbSet access
        if (receiver.Contains("Set<") || receiver.Contains("DbSet"))
        {
            return true;
        }

        return false;
    }

    private bool IsDatabaseMutation(string? receiver, string methodName)
    {
        if (receiver == null)
        {
            return false;
        }

        // SaveChanges is always a DB mutation
        if (methodName.StartsWith("SaveChanges"))
        {
            return true;
        }

        // Check for DbContext patterns
        if (receiver.Contains("_context") || receiver.Contains("_dbContext") ||
            receiver.Contains("_db") || receiver.Contains("DbContext") ||
            receiver.Contains("Context"))
        {
            return true;
        }

        // Check for Dapper Execute patterns
        if (methodName.StartsWith("Execute") &&
            (receiver.Contains("connection") || receiver.Contains("Connection")))
        {
            return true;
        }

        return false;
    }

    private bool IsNavigationProperty(PropertyDeclarationSyntax property)
    {
        var typeName = property.Type.ToString();

        // Collection navigation properties
        if (typeName.StartsWith("ICollection<") || typeName.StartsWith("List<") ||
            typeName.StartsWith("IList<") || typeName.StartsWith("IEnumerable<") ||
            typeName.StartsWith("HashSet<"))
        {
            return true;
        }

        // Check for virtual keyword (often used for lazy loading)
        if (property.Modifiers.Any(m => m.Text == "virtual"))
        {
            return true;
        }

        return false;
    }

    private DatabaseEntityType DetermineEntityType(ClassDeclarationSyntax classDecl)
    {
        var attributes = classDecl.AttributeLists.SelectMany(al => al.Attributes).ToList();

        if (attributes.Any(a => a.Name.ToString().Contains("Keyless")))
        {
            return DatabaseEntityType.Keyless;
        }

        if (attributes.Any(a => a.Name.ToString().Contains("Owned")))
        {
            return DatabaseEntityType.Owned;
        }

        return DatabaseEntityType.Table;
    }

    private DatabaseQueryType DetermineQueryType(string methodName)
    {
        if (methodName.StartsWith("FromSql"))
        {
            return DatabaseQueryType.RawSql;
        }

        if (methodName.StartsWith("Query") && !methodName.Contains("Multiple"))
        {
            return DatabaseQueryType.Dapper;
        }

        if (methodName == "Include" || methodName == "ThenInclude")
        {
            return DatabaseQueryType.EagerLoad;
        }

        if (methodName.StartsWith("First") || methodName.StartsWith("Single"))
        {
            return DatabaseQueryType.SingleResult;
        }

        if (methodName.StartsWith("ToList") || methodName.StartsWith("ToArray"))
        {
            return DatabaseQueryType.MultipleResults;
        }

        if (methodName == "Any" || methodName == "AnyAsync" ||
            methodName == "All" || methodName == "AllAsync")
        {
            return DatabaseQueryType.Existence;
        }

        if (methodName == "Count" || methodName == "CountAsync" ||
            methodName.Contains("Sum") || methodName.Contains("Average") ||
            methodName.Contains("Min") || methodName.Contains("Max"))
        {
            return DatabaseQueryType.Aggregate;
        }

        return DatabaseQueryType.Linq;
    }

    private DatabaseMutationType DetermineMutationType(string methodName)
    {
        if (methodName.StartsWith("Add"))
        {
            return DatabaseMutationType.Insert;
        }

        if (methodName.StartsWith("Update") || methodName.StartsWith("ExecuteUpdate"))
        {
            return DatabaseMutationType.Update;
        }

        if (methodName.StartsWith("Remove") || methodName.StartsWith("ExecuteDelete"))
        {
            return DatabaseMutationType.Delete;
        }

        if (methodName.StartsWith("SaveChanges"))
        {
            return DatabaseMutationType.SaveChanges;
        }

        if (methodName.StartsWith("Execute"))
        {
            return DatabaseMutationType.Execute;
        }

        if (methodName.StartsWith("Attach"))
        {
            return DatabaseMutationType.Attach;
        }

        return DatabaseMutationType.Unknown;
    }

    private string? ExtractEntityType(InvocationExpressionSyntax invocation)
    {
        // Check for generic type argument
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName)
        {
            var typeArg = genericName.TypeArgumentList.Arguments.FirstOrDefault();
            if (typeArg != null)
            {
                return typeArg.ToString();
            }
        }

        // Try to infer from receiver (e.g., _context.Users -> User)
        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var receiver = ma.Expression.ToString();
            var parts = receiver.Split('.');
            if (parts.Length > 0)
            {
                var lastPart = parts.Last();
                // Remove plural 's' to get entity name
                if (lastPart.EndsWith("s") && !lastPart.EndsWith("ss"))
                {
                    return lastPart.TrimEnd('s');
                }
            }
        }

        return null;
    }

    private float CalculateQueryConfidence(string methodName, string? receiver)
    {
        var confidence = 0.85f;

        if (receiver?.Contains("_context") == true || receiver?.Contains("DbContext") == true)
        {
            confidence = 0.95f;
        }
        else if (methodName.StartsWith("FromSql"))
        {
            confidence = 0.90f;
        }
        else if (methodName.StartsWith("Query"))
        {
            confidence = 0.85f; // Dapper
        }

        return confidence;
    }

    private float CalculateMutationConfidence(string methodName, string? receiver)
    {
        var confidence = 0.85f;

        if (methodName.StartsWith("SaveChanges"))
        {
            confidence = 0.95f;
        }
        else if (receiver?.Contains("_context") == true || receiver?.Contains("DbContext") == true)
        {
            confidence = 0.90f;
        }

        return confidence;
    }
}

/// <summary>
/// Information about a detected database entity.
/// </summary>
public record DatabaseEntityInfo
{
    /// <summary>Gets the class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Gets the database table name.</summary>
    public required string TableName { get; init; }

    /// <summary>Gets the entity type.</summary>
    public required DatabaseEntityType EntityType { get; init; }

    /// <summary>Gets the entity properties.</summary>
    public required IReadOnlyList<EntityPropertyInfo> Properties { get; init; }

    /// <summary>Gets the DbContext class if detected via DbSet.</summary>
    public string? DbContextClass { get; init; }

    /// <summary>Gets the DbSet property name.</summary>
    public string? DbSetPropertyName { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets the detection confidence.</summary>
    public required float Confidence { get; init; }
}

/// <summary>
/// Information about an entity property.
/// </summary>
public record EntityPropertyInfo
{
    /// <summary>Gets the property name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the property type.</summary>
    public required string Type { get; init; }

    /// <summary>Gets whether this is a key property.</summary>
    public bool IsKey { get; init; }

    /// <summary>Gets whether this is a navigation property.</summary>
    public bool IsNavigation { get; init; }
}

/// <summary>
/// Information about a detected database query.
/// </summary>
public record DatabaseQueryInfo
{
    /// <summary>Gets the query type.</summary>
    public required DatabaseQueryType QueryType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public string? ReceiverExpression { get; init; }

    /// <summary>Gets the entity type being queried.</summary>
    public string? EntityType { get; init; }

    /// <summary>Gets whether the query is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets whether the query uses raw SQL.</summary>
    public required bool UsesRawSql { get; init; }

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
/// Information about a detected database mutation.
/// </summary>
public record DatabaseMutationInfo
{
    /// <summary>Gets the mutation type.</summary>
    public required DatabaseMutationType MutationType { get; init; }

    /// <summary>Gets the method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>Gets the receiver expression.</summary>
    public string? ReceiverExpression { get; init; }

    /// <summary>Gets the entity type being mutated.</summary>
    public string? EntityType { get; init; }

    /// <summary>Gets whether the mutation is async.</summary>
    public required bool IsAsync { get; init; }

    /// <summary>Gets whether this is a bulk operation.</summary>
    public required bool IsBulkOperation { get; init; }

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
/// Types of database entities.
/// </summary>
public enum DatabaseEntityType
{
    /// <summary>Regular table entity with [Table] attribute.</summary>
    Table,

    /// <summary>Entity detected via DbSet property.</summary>
    DbSetEntity,

    /// <summary>Keyless entity (view or query type).</summary>
    Keyless,

    /// <summary>Owned entity type.</summary>
    Owned,
}

/// <summary>
/// Types of database queries.
/// </summary>
public enum DatabaseQueryType
{
    /// <summary>LINQ query.</summary>
    Linq,

    /// <summary>Single result query (First, Single).</summary>
    SingleResult,

    /// <summary>Multiple results query (ToList, ToArray).</summary>
    MultipleResults,

    /// <summary>Eager loading query (Include).</summary>
    EagerLoad,

    /// <summary>Aggregate query (Count, Sum, etc.).</summary>
    Aggregate,

    /// <summary>Existence check (Any, All).</summary>
    Existence,

    /// <summary>Raw SQL query.</summary>
    RawSql,

    /// <summary>Dapper query.</summary>
    Dapper,
}

/// <summary>
/// Types of database mutations.
/// </summary>
public enum DatabaseMutationType
{
    /// <summary>Insert operation.</summary>
    Insert,

    /// <summary>Update operation.</summary>
    Update,

    /// <summary>Delete operation.</summary>
    Delete,

    /// <summary>SaveChanges call.</summary>
    SaveChanges,

    /// <summary>Raw execute (Dapper).</summary>
    Execute,

    /// <summary>Attach entity.</summary>
    Attach,

    /// <summary>Unknown mutation.</summary>
    Unknown,
}
