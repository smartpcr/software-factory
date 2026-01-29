// -----------------------------------------------------------------------
// <copyright file="InheritanceAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using CodeIntelligence.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes inheritance relationships in C# code, building full hierarchy graphs.
/// </summary>
public sealed class InheritanceAnalyzer
{
    private readonly Dictionary<string, TypeHierarchyNode> typeNodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Analyzes a syntax tree for inheritance relationships.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected inheritance relationships.</returns>
    public IReadOnlyList<InheritanceRelationship> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var relationships = new List<InheritanceRelationship>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassDeclarationSyntax classDecl:
                    relationships.AddRange(this.AnalyzeClassDeclaration(classDecl, filePath));
                    break;

                case RecordDeclarationSyntax recordDecl:
                    relationships.AddRange(this.AnalyzeRecordDeclaration(recordDecl, filePath));
                    break;

                case InterfaceDeclarationSyntax interfaceDecl:
                    relationships.AddRange(this.AnalyzeInterfaceDeclaration(interfaceDecl, filePath));
                    break;

                case StructDeclarationSyntax structDecl:
                    relationships.AddRange(this.AnalyzeStructDeclaration(structDecl, filePath));
                    break;
            }
        }

        return relationships;
    }

    /// <summary>
    /// Builds a complete type hierarchy from all analyzed relationships.
    /// </summary>
    /// <returns>The type hierarchy.</returns>
    public TypeHierarchy BuildHierarchy()
    {
        return new TypeHierarchy
        {
            Types = this.typeNodes.Values.ToList(),
            RootTypes = this.typeNodes.Values.Where(t => t.BaseType == null).ToList()
        };
    }

    /// <summary>
    /// Gets or creates a type node in the hierarchy.
    /// </summary>
    private TypeHierarchyNode GetOrCreateNode(string fullName, TypeKind kind)
    {
        if (!this.typeNodes.TryGetValue(fullName, out var node))
        {
            node = new TypeHierarchyNode
            {
                FullName = fullName,
                Name = fullName.Contains('.') ? fullName[(fullName.LastIndexOf('.') + 1)..] : fullName,
                Kind = kind
            };
            this.typeNodes[fullName] = node;
        }

        return node;
    }

    private IEnumerable<InheritanceRelationship> AnalyzeClassDeclaration(
        ClassDeclarationSyntax classDecl,
        string filePath)
    {
        var className = classDecl.Identifier.Text;
        var namespaceName = GetNamespace(classDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? className : $"{namespaceName}.{className}";

        var classNode = this.GetOrCreateNode(fullName, TypeKind.Class);
        classNode.FilePath = filePath;
        classNode.LineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        classNode.IsAbstract = classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        classNode.IsSealed = classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));
        classNode.IsStatic = classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        classNode.IsPartial = classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        classNode.GenericParameters = GetGenericParameters(classDecl.TypeParameterList);

        if (classDecl.BaseList == null)
        {
            yield break;
        }

        foreach (var baseType in classDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();
            var isInterface = baseTypeName.StartsWith("I") && baseTypeName.Length > 1 && char.IsUpper(baseTypeName[1]);

            if (isInterface)
            {
                var interfaceNode = this.GetOrCreateNode(baseTypeName, TypeKind.Interface);
                classNode.ImplementedInterfaces.Add(interfaceNode);

                yield return new InheritanceRelationship
                {
                    SourceType = fullName,
                    TargetType = baseTypeName,
                    RelationType = InheritanceRelationType.Implements,
                    SourceKind = TypeKind.Class,
                    TargetKind = TypeKind.Interface,
                    FilePath = filePath,
                    LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsGeneric = baseTypeName.Contains('<'),
                    GenericArguments = ExtractGenericArguments(baseTypeName)
                };
            }
            else if (classNode.BaseType == null)
            {
                var baseNode = this.GetOrCreateNode(baseTypeName, TypeKind.Class);
                classNode.BaseType = baseNode;
                baseNode.DerivedTypes.Add(classNode);

                yield return new InheritanceRelationship
                {
                    SourceType = fullName,
                    TargetType = baseTypeName,
                    RelationType = InheritanceRelationType.Inherits,
                    SourceKind = TypeKind.Class,
                    TargetKind = TypeKind.Class,
                    FilePath = filePath,
                    LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsGeneric = baseTypeName.Contains('<'),
                    GenericArguments = ExtractGenericArguments(baseTypeName)
                };
            }
        }
    }

    private IEnumerable<InheritanceRelationship> AnalyzeRecordDeclaration(
        RecordDeclarationSyntax recordDecl,
        string filePath)
    {
        var recordName = recordDecl.Identifier.Text;
        var namespaceName = GetNamespace(recordDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? recordName : $"{namespaceName}.{recordName}";

        var recordNode = this.GetOrCreateNode(fullName, TypeKind.Record);
        recordNode.FilePath = filePath;
        recordNode.LineNumber = recordDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        recordNode.IsAbstract = recordDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        recordNode.IsSealed = recordDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.SealedKeyword));
        recordNode.GenericParameters = GetGenericParameters(recordDecl.TypeParameterList);

        if (recordDecl.BaseList == null)
        {
            yield break;
        }

        foreach (var baseType in recordDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();
            var isInterface = baseTypeName.StartsWith("I") && baseTypeName.Length > 1 && char.IsUpper(baseTypeName[1]);

            if (isInterface)
            {
                var interfaceNode = this.GetOrCreateNode(baseTypeName, TypeKind.Interface);
                recordNode.ImplementedInterfaces.Add(interfaceNode);

                yield return new InheritanceRelationship
                {
                    SourceType = fullName,
                    TargetType = baseTypeName,
                    RelationType = InheritanceRelationType.Implements,
                    SourceKind = TypeKind.Record,
                    TargetKind = TypeKind.Interface,
                    FilePath = filePath,
                    LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };
            }
            else if (recordNode.BaseType == null)
            {
                var baseNode = this.GetOrCreateNode(baseTypeName, TypeKind.Record);
                recordNode.BaseType = baseNode;
                baseNode.DerivedTypes.Add(recordNode);

                yield return new InheritanceRelationship
                {
                    SourceType = fullName,
                    TargetType = baseTypeName,
                    RelationType = InheritanceRelationType.Inherits,
                    SourceKind = TypeKind.Record,
                    TargetKind = TypeKind.Record,
                    FilePath = filePath,
                    LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };
            }
        }
    }

    private IEnumerable<InheritanceRelationship> AnalyzeInterfaceDeclaration(
        InterfaceDeclarationSyntax interfaceDecl,
        string filePath)
    {
        var interfaceName = interfaceDecl.Identifier.Text;
        var namespaceName = GetNamespace(interfaceDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? interfaceName : $"{namespaceName}.{interfaceName}";

        var interfaceNode = this.GetOrCreateNode(fullName, TypeKind.Interface);
        interfaceNode.FilePath = filePath;
        interfaceNode.LineNumber = interfaceDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        interfaceNode.GenericParameters = GetGenericParameters(interfaceDecl.TypeParameterList);

        if (interfaceDecl.BaseList == null)
        {
            yield break;
        }

        foreach (var baseType in interfaceDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();
            var baseNode = this.GetOrCreateNode(baseTypeName, TypeKind.Interface);
            interfaceNode.ImplementedInterfaces.Add(baseNode);

            yield return new InheritanceRelationship
            {
                SourceType = fullName,
                TargetType = baseTypeName,
                RelationType = InheritanceRelationType.Extends,
                SourceKind = TypeKind.Interface,
                TargetKind = TypeKind.Interface,
                FilePath = filePath,
                LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
    }

    private IEnumerable<InheritanceRelationship> AnalyzeStructDeclaration(
        StructDeclarationSyntax structDecl,
        string filePath)
    {
        var structName = structDecl.Identifier.Text;
        var namespaceName = GetNamespace(structDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? structName : $"{namespaceName}.{structName}";

        var structNode = this.GetOrCreateNode(fullName, TypeKind.Struct);
        structNode.FilePath = filePath;
        structNode.LineNumber = structDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        structNode.GenericParameters = GetGenericParameters(structDecl.TypeParameterList);

        if (structDecl.BaseList == null)
        {
            yield break;
        }

        // Structs can only implement interfaces
        foreach (var baseType in structDecl.BaseList.Types)
        {
            var baseTypeName = baseType.Type.ToString();
            var interfaceNode = this.GetOrCreateNode(baseTypeName, TypeKind.Interface);
            structNode.ImplementedInterfaces.Add(interfaceNode);

            yield return new InheritanceRelationship
            {
                SourceType = fullName,
                TargetType = baseTypeName,
                RelationType = InheritanceRelationType.Implements,
                SourceKind = TypeKind.Struct,
                TargetKind = TypeKind.Interface,
                FilePath = filePath,
                LineNumber = baseType.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is BaseNamespaceDeclarationSyntax nsDecl)
            {
                return nsDecl.Name.ToString();
            }

            current = current.Parent;
        }

        return string.Empty;
    }

    private static List<string> GetGenericParameters(TypeParameterListSyntax? typeParameterList)
    {
        if (typeParameterList == null)
        {
            return new List<string>();
        }

        return typeParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
    }

    private static List<string> ExtractGenericArguments(string typeName)
    {
        var start = typeName.IndexOf('<');
        if (start < 0)
        {
            return new List<string>();
        }

        var end = typeName.LastIndexOf('>');
        if (end <= start)
        {
            return new List<string>();
        }

        var args = typeName[(start + 1)..end];
        return args.Split(',').Select(a => a.Trim()).ToList();
    }
}

/// <summary>
/// Represents an inheritance relationship between types.
/// </summary>
public record InheritanceRelationship
{
    /// <summary>Gets the source type (the one that inherits/implements).</summary>
    public required string SourceType { get; init; }

    /// <summary>Gets the target type (the base class or interface).</summary>
    public required string TargetType { get; init; }

    /// <summary>Gets the type of relationship.</summary>
    public required InheritanceRelationType RelationType { get; init; }

    /// <summary>Gets the kind of the source type.</summary>
    public required TypeKind SourceKind { get; init; }

    /// <summary>Gets the kind of the target type.</summary>
    public required TypeKind TargetKind { get; init; }

    /// <summary>Gets the file path where this relationship is defined.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Gets whether this involves generic types.</summary>
    public bool IsGeneric { get; init; }

    /// <summary>Gets the generic type arguments if any.</summary>
    public List<string> GenericArguments { get; init; } = new();
}

/// <summary>
/// Types of inheritance relationships.
/// </summary>
public enum InheritanceRelationType
{
    /// <summary>Class inherits from a base class.</summary>
    Inherits,

    /// <summary>Type implements an interface.</summary>
    Implements,

    /// <summary>Interface extends another interface.</summary>
    Extends
}

/// <summary>
/// Kinds of types in the type system.
/// </summary>
public enum TypeKind
{
    /// <summary>A class.</summary>
    Class,

    /// <summary>An interface.</summary>
    Interface,

    /// <summary>A struct.</summary>
    Struct,

    /// <summary>A record.</summary>
    Record,

    /// <summary>An enum.</summary>
    Enum,

    /// <summary>A delegate.</summary>
    Delegate
}

/// <summary>
/// A node in the type hierarchy graph.
/// </summary>
public class TypeHierarchyNode
{
    /// <summary>Gets or sets the full name including namespace.</summary>
    public required string FullName { get; set; }

    /// <summary>Gets or sets the simple name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the kind of type.</summary>
    public required TypeKind Kind { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the line number.</summary>
    public int LineNumber { get; set; }

    /// <summary>Gets or sets the base type (for classes/records).</summary>
    public TypeHierarchyNode? BaseType { get; set; }

    /// <summary>Gets the implemented interfaces.</summary>
    public List<TypeHierarchyNode> ImplementedInterfaces { get; } = new();

    /// <summary>Gets the derived types.</summary>
    public List<TypeHierarchyNode> DerivedTypes { get; } = new();

    /// <summary>Gets or sets whether this is an abstract type.</summary>
    public bool IsAbstract { get; set; }

    /// <summary>Gets or sets whether this is a sealed type.</summary>
    public bool IsSealed { get; set; }

    /// <summary>Gets or sets whether this is a static type.</summary>
    public bool IsStatic { get; set; }

    /// <summary>Gets or sets whether this is a partial type.</summary>
    public bool IsPartial { get; set; }

    /// <summary>Gets or sets the generic type parameters.</summary>
    public List<string> GenericParameters { get; set; } = new();
}

/// <summary>
/// Complete type hierarchy for a codebase.
/// </summary>
public record TypeHierarchy
{
    /// <summary>Gets all types in the hierarchy.</summary>
    public required IReadOnlyList<TypeHierarchyNode> Types { get; init; }

    /// <summary>Gets root types (types with no base class).</summary>
    public required IReadOnlyList<TypeHierarchyNode> RootTypes { get; init; }
}
