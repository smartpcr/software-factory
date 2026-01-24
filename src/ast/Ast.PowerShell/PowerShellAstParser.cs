// -----------------------------------------------------------------------
// <copyright file="PowerShellAstParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell;

using System.Management.Automation.Language;

public class PowerShellAstParser
{
    public IEnumerable<PsAstNode> ParseFile(string filePath)
    {
        var code = File.ReadAllText(filePath);
        return this.ParseCode(code, filePath);
    }

    public IEnumerable<PsAstNode> ParseCode(string code, string? filePath = null)
    {
        var ast = Parser.ParseInput(code, out var tokens, out var errors);

        if (errors.Length > 0)
        {
            foreach (var error in errors)
            {
                yield return new PsAstNode
                {
                    NodeType = PsAstNodeType.Error,
                    Name = "ParseError",
                    FilePath = filePath ?? "unknown",
                    StartLine = error.Extent.StartLineNumber,
                    EndLine = error.Extent.EndLineNumber,
                    Code = error.Message
                };
            }
        }

        foreach (var node in this.ExtractNodes(ast, filePath ?? "unknown"))
        {
            yield return node;
        }
    }

    public IEnumerable<PsAstNode> ParseRepository(string repositoryPath, string pattern = "*.ps1")
    {
        var patterns = new[] { "*.ps1", "*.psm1", "*.psd1" };

        foreach (var p in patterns)
        {
            var files = Directory.GetFiles(repositoryPath, p, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                foreach (var node in this.ParseFile(file))
                {
                    yield return node;
                }
            }
        }
    }

    private IEnumerable<PsAstNode> ExtractNodes(System.Management.Automation.Language.Ast ast, string filePath)
    {
        foreach (var node in ast.FindAll(n => true, true))
        {
            var result = node switch
            {
                FunctionDefinitionAst func => CreateFunctionNode(func, filePath),
                TypeDefinitionAst typeDef => CreateTypeDefinitionNode(typeDef, filePath),
                ParamBlockAst param => CreateParamBlockNode(param, filePath),
                ScriptBlockAst script when script.Parent is FunctionDefinitionAst => null,
                ScriptBlockAst script => CreateScriptBlockNode(script, filePath),
                _ => null
            };

            if (result != null)
            {
                yield return result;
            }
        }
    }

    private static PsAstNode CreateFunctionNode(FunctionDefinitionAst func, string filePath)
    {
        var parameters = new List<PsParameterInfo>();

        if (func.Body.ParamBlock != null)
        {
            foreach (var param in func.Body.ParamBlock.Parameters)
            {
                parameters.Add(new PsParameterInfo
                {
                    Name = param.Name.VariablePath.UserPath,
                    Type = param.StaticType?.Name ?? "object",
                    IsMandatory = param.Attributes.OfType<AttributeAst>()
                        .Any(a => a.TypeName.Name == "Parameter" &&
                                  a.NamedArguments.Any(n => n.ArgumentName == "Mandatory"))
                });
            }
        }

        return new PsAstNode
        {
            NodeType = func.IsWorkflow ? PsAstNodeType.Workflow : PsAstNodeType.Function,
            Name = func.Name,
            FilePath = filePath,
            StartLine = func.Extent.StartLineNumber,
            EndLine = func.Extent.EndLineNumber,
            Code = func.Extent.Text,
            Summary = ExtractHelpComment(func),
            Parameters = parameters
        };
    }

    private static PsAstNode CreateTypeDefinitionNode(TypeDefinitionAst typeDef, string filePath)
    {
        var methods = new List<string>();
        var properties = new List<string>();

        foreach (var member in typeDef.Members)
        {
            if (member is FunctionMemberAst method)
            {
                methods.Add(method.Name);
            }
            else if (member is PropertyMemberAst prop)
            {
                properties.Add(prop.Name);
            }
        }

        var nodeType = typeDef.IsEnum ? PsAstNodeType.Enum : PsAstNodeType.Class;

        return new PsAstNode
        {
            NodeType = nodeType,
            Name = typeDef.Name,
            FilePath = filePath,
            StartLine = typeDef.Extent.StartLineNumber,
            EndLine = typeDef.Extent.EndLineNumber,
            Code = typeDef.Extent.Text,
            BaseTypes = typeDef.BaseTypes?.Select(b => b.TypeName.Name).ToList() ?? [],
            Methods = methods,
            Properties = properties
        };
    }

    private static PsAstNode CreateParamBlockNode(ParamBlockAst param, string filePath)
    {
        var parameters = param.Parameters.Select(p => new PsParameterInfo
        {
            Name = p.Name.VariablePath.UserPath,
            Type = p.StaticType?.Name ?? "object",
            IsMandatory = p.Attributes.OfType<AttributeAst>()
                .Any(a => a.TypeName.Name == "Parameter" &&
                          a.NamedArguments.Any(n => n.ArgumentName == "Mandatory"))
        }).ToList();

        return new PsAstNode
        {
            NodeType = PsAstNodeType.ParamBlock,
            Name = "ParamBlock",
            FilePath = filePath,
            StartLine = param.Extent.StartLineNumber,
            EndLine = param.Extent.EndLineNumber,
            Code = param.Extent.Text,
            Parameters = parameters
        };
    }

    private static PsAstNode CreateScriptBlockNode(ScriptBlockAst script, string filePath)
    {
        return new PsAstNode
        {
            NodeType = PsAstNodeType.ScriptBlock,
            Name = Path.GetFileName(filePath),
            FilePath = filePath,
            StartLine = script.Extent.StartLineNumber,
            EndLine = script.Extent.EndLineNumber,
            Code = script.Extent.Text,
            Summary = ExtractScriptHelp(script)
        };
    }

    private static string? ExtractHelpComment(FunctionDefinitionAst func)
    {
        var helpContent = func.GetHelpContent();
        return helpContent?.Synopsis;
    }

    private static string? ExtractScriptHelp(ScriptBlockAst script)
    {
        var helpContent = script.GetHelpContent();
        return helpContent?.Synopsis;
    }
}