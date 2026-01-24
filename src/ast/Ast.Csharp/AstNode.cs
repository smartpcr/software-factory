// -----------------------------------------------------------------------
// <copyright file="AstNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp;

public class AstNode
{
    public required AstNodeType NodeType { get; set; }
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Code { get; set; }
    public string? Summary { get; set; }
    public string? Modifiers { get; set; }
    public string? ReturnType { get; set; }
    public List<string> BaseTypes { get; set; } = [];
    public List<ParameterInfo> Parameters { get; set; } = [];

    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"{this.NodeType}: {this.Name}",
            $"File: {this.FilePath}:{this.StartLine}"
        };

        if (!string.IsNullOrEmpty(this.Summary))
        {
            parts.Add($"Summary: {this.Summary}");
        }

        if (!string.IsNullOrEmpty(this.Modifiers))
        {
            parts.Add($"Modifiers: {this.Modifiers}");
        }

        if (!string.IsNullOrEmpty(this.ReturnType))
        {
            parts.Add($"Returns: {this.ReturnType}");
        }

        if (this.BaseTypes.Count > 0)
        {
            parts.Add($"Inherits: {string.Join(", ", this.BaseTypes)}");
        }

        if (this.Parameters.Count > 0)
        {
            parts.Add($"Parameters: {string.Join(", ", this.Parameters.Select(p => $"{p.Type} {p.Name}"))}");
        }

        if (!string.IsNullOrEmpty(this.Code))
        {
            parts.Add($"Code:\n{this.Code}");
        }

        return string.Join("\n", parts);
    }
}

public class ParameterInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
}

public enum AstNodeType
{
    Namespace,
    Class,
    Record,
    Struct,
    Interface,
    Enum,
    Method,
    Property,
    Field,
    Constructor
}