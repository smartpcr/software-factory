// -----------------------------------------------------------------------
// <copyright file="PsAstNode.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell;

public class PsAstNode
{
    public required PsAstNodeType NodeType { get; set; }
    public required string Name { get; set; }
    public required string FilePath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Code { get; set; }
    public string? Summary { get; set; }
    public List<string> BaseTypes { get; set; } = new();
    public List<PsParameterInfo> Parameters { get; set; } = new();
    public List<string> Methods { get; set; } = new();
    public List<string> Properties { get; set; } = new();

    public string GetEmbeddingText()
    {
        var parts = new List<string>
        {
            $"{this.NodeType}: {this.Name}",
            $"File: {this.FilePath}:{this.StartLine}"
        };

        if (!string.IsNullOrEmpty(this.Summary))
        {
            parts.Add($"Synopsis: {this.Summary}");
        }

        if (this.BaseTypes.Count > 0)
        {
            parts.Add($"Inherits: {string.Join(", ", this.BaseTypes)}");
        }

        if (this.Parameters.Count > 0)
        {
            var paramStrings = this.Parameters.Select(p =>
            {
                var mandatory = p.IsMandatory ? "[Mandatory]" : "";
                return $"{mandatory}{p.Type} ${p.Name}".Trim();
            });
            parts.Add($"Parameters: {string.Join(", ", paramStrings)}");
        }

        if (this.Methods.Count > 0)
        {
            parts.Add($"Methods: {string.Join(", ", this.Methods)}");
        }

        if (this.Properties.Count > 0)
        {
            parts.Add($"Properties: {string.Join(", ", this.Properties)}");
        }

        if (!string.IsNullOrEmpty(this.Code))
        {
            parts.Add($"Code:\n{this.Code}");
        }

        return string.Join("\n", parts);
    }
}

public class PsParameterInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public bool IsMandatory { get; set; }
}

public enum PsAstNodeType
{
    Function,
    Workflow,
    Class,
    Enum,
    ScriptBlock,
    ParamBlock,
    Error
}