// -----------------------------------------------------------------------
// <copyright file="CsharpAstParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class CsharpAstParser
{
    public IEnumerable<AstNode> ParseFile(string filePath)
    {
        var code = File.ReadAllText(filePath);
        return this.ParseCode(code, filePath);
    }

    public IEnumerable<AstNode> ParseCode(string code, string? filePath = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetCompilationUnitRoot();

        return this.ExtractNodes(root, filePath ?? "unknown");
    }

    public IEnumerable<AstNode> ParseRepository(string repositoryPath, string pattern = "*.cs")
    {
        var files = Directory.GetFiles(repositoryPath, pattern, SearchOption.AllDirectories);

        foreach (var file in files)
        {
            foreach (var node in this.ParseFile(file))
            {
                yield return node;
            }
        }
    }

    private IEnumerable<AstNode> ExtractNodes(CompilationUnitSyntax root, string filePath)
    {
        foreach (var member in root.DescendantNodes())
        {
            var node = member switch
            {
                NamespaceDeclarationSyntax ns => this.CreateNamespaceNode(ns, filePath),
                FileScopedNamespaceDeclarationSyntax ns => this.CreateFileScopedNamespaceNode(ns, filePath),
                ClassDeclarationSyntax cls => this.CreateClassNode(cls, filePath),
                RecordDeclarationSyntax rec => this.CreateRecordNode(rec, filePath),
                StructDeclarationSyntax str => this.CreateStructNode(str, filePath),
                InterfaceDeclarationSyntax iface => this.CreateInterfaceNode(iface, filePath),
                EnumDeclarationSyntax enm => this.CreateEnumNode(enm, filePath),
                MethodDeclarationSyntax method => this.CreateMethodNode(method, filePath),
                PropertyDeclarationSyntax prop => this.CreatePropertyNode(prop, filePath),
                FieldDeclarationSyntax field => this.CreateFieldNode(field, filePath),
                ConstructorDeclarationSyntax ctor => this.CreateConstructorNode(ctor, filePath),
                _ => null
            };

            if (node != null)
            {
                yield return node;
            }
        }
    }

    private AstNode CreateNamespaceNode(NamespaceDeclarationSyntax ns, string filePath)
    {
        var lineSpan = ns.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Namespace,
            Name = ns.Name.ToString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = ns.ToString(),
            Summary = GetDocumentationSummary(ns)
        };
    }

    private AstNode CreateFileScopedNamespaceNode(FileScopedNamespaceDeclarationSyntax ns, string filePath)
    {
        var lineSpan = ns.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Namespace,
            Name = ns.Name.ToString(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = ns.ToString(),
            Summary = GetDocumentationSummary(ns)
        };
    }

    private AstNode CreateClassNode(ClassDeclarationSyntax cls, string filePath)
    {
        var lineSpan = cls.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Class,
            Name = cls.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = cls.ToString(),
            Summary = GetDocumentationSummary(cls),
            Modifiers = cls.Modifiers.ToString(),
            BaseTypes = cls.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>()
        };
    }

    private AstNode CreateRecordNode(RecordDeclarationSyntax rec, string filePath)
    {
        var lineSpan = rec.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Record,
            Name = rec.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = rec.ToString(),
            Summary = GetDocumentationSummary(rec),
            Modifiers = rec.Modifiers.ToString(),
            BaseTypes = rec.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>()
        };
    }

    private AstNode CreateStructNode(StructDeclarationSyntax str, string filePath)
    {
        var lineSpan = str.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Struct,
            Name = str.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = str.ToString(),
            Summary = GetDocumentationSummary(str),
            Modifiers = str.Modifiers.ToString(),
            BaseTypes = str.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>()
        };
    }

    private AstNode CreateInterfaceNode(InterfaceDeclarationSyntax iface, string filePath)
    {
        var lineSpan = iface.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Interface,
            Name = iface.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = iface.ToString(),
            Summary = GetDocumentationSummary(iface),
            Modifiers = iface.Modifiers.ToString(),
            BaseTypes = iface.BaseList?.Types.Select(t => t.ToString()).ToList() ?? new List<string>()
        };
    }

    private AstNode CreateEnumNode(EnumDeclarationSyntax enm, string filePath)
    {
        var lineSpan = enm.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Enum,
            Name = enm.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = enm.ToString(),
            Summary = GetDocumentationSummary(enm),
            Modifiers = enm.Modifiers.ToString()
        };
    }

    private AstNode CreateMethodNode(MethodDeclarationSyntax method, string filePath)
    {
        var lineSpan = method.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Method,
            Name = method.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = method.ToString(),
            Summary = GetDocumentationSummary(method),
            Modifiers = method.Modifiers.ToString(),
            ReturnType = method.ReturnType.ToString(),
            Parameters = method.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "unknown"
            }).ToList()
        };
    }

    private AstNode CreatePropertyNode(PropertyDeclarationSyntax prop, string filePath)
    {
        var lineSpan = prop.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Property,
            Name = prop.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = prop.ToString(),
            Summary = GetDocumentationSummary(prop),
            Modifiers = prop.Modifiers.ToString(),
            ReturnType = prop.Type.ToString()
        };
    }

    private AstNode CreateFieldNode(FieldDeclarationSyntax field, string filePath)
    {
        var lineSpan = field.GetLocation().GetLineSpan();
        var variableName = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "unknown";
        return new AstNode
        {
            NodeType = AstNodeType.Field,
            Name = variableName,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = field.ToString(),
            Summary = GetDocumentationSummary(field),
            Modifiers = field.Modifiers.ToString(),
            ReturnType = field.Declaration.Type.ToString()
        };
    }

    private AstNode CreateConstructorNode(ConstructorDeclarationSyntax ctor, string filePath)
    {
        var lineSpan = ctor.GetLocation().GetLineSpan();
        return new AstNode
        {
            NodeType = AstNodeType.Constructor,
            Name = ctor.Identifier.Text,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Code = ctor.ToString(),
            Summary = GetDocumentationSummary(ctor),
            Modifiers = ctor.Modifiers.ToString(),
            Parameters = ctor.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "unknown"
            }).ToList()
        };
    }

    private static string? GetDocumentationSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (trivia == null) return null;

        var summaryElement = trivia.ChildNodes()
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");

        return summaryElement?.Content.ToString().Trim();
    }
}