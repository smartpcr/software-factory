// -----------------------------------------------------------------------
// <copyright file="PowerShellHyperedgeExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.PowerShell;

using System.Management.Automation.Language;
using System.Security.Cryptography;
using System.Text;
using Ast.Core;

/// <summary>
/// Extracts hyperedges from PowerShell scripts using the PowerShell AST.
///
/// Key PowerShell patterns captured:
/// - Functions with param blocks (ALL parameters in one hyperedge)
/// - Cmdlet calls with ALL parameters
/// - Pipelines (entire chain as single hyperedge)
/// - DSC configurations and resources
/// </summary>
public class PowerShellHyperedgeExtractor
{
    private readonly Dictionary<string, HypergraphNode> nodes = [];
    private readonly List<Hyperedge> hyperedges = [];
    private readonly string filePath;
    private readonly Stack<string> scopeStack = new();
    private int edgeCounter;

    private readonly Dictionary<string, List<string>> variableDefs = [];
    private readonly Dictionary<string, List<string>> variableUses = [];

    // Track imported modules for cross-module call resolution
    private readonly Dictionary<string, string> importedModules = []; // alias -> module name
    private readonly List<string> loadedModules = [];

    // Remote execution cmdlets to track
    private static readonly HashSet<string> remoteExecutionCmdlets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-Command",
        "Enter-PSSession",
        "New-PSSession",
        "Connect-PSSession",
        "Receive-PSSession",
        "Get-PSSession",
        "Remove-PSSession",
        "Disconnect-PSSession",
        "Export-PSSession",
        "Import-PSSession"
    };

    public PowerShellHyperedgeExtractor(string filePath)
    {
        this.filePath = filePath;
        this.scopeStack.Push(Path.GetFileNameWithoutExtension(filePath));
    }

    private string CurrentScope => string.Join(".", this.scopeStack.Reverse());

    public (Dictionary<string, HypergraphNode> Nodes, List<Hyperedge> Hyperedges) Extract(string sourceCode)
    {
        var ast = Parser.ParseInput(sourceCode, out var tokens, out var errors);

        // Extract all constructs
        this.ExtractUsingStatements(ast);
        this.ExtractImportModule(ast);
        this.ExtractFunctions(ast);
        this.ExtractClasses(ast);
        this.ExtractCommands(ast);
        this.ExtractModuleQualifiedCalls(ast);
        this.ExtractRemoteExecution(ast);
        this.ExtractPipelines(ast);
        this.ExtractAssignments(ast);
        this.ExtractLoops(ast);
        this.ExtractTryCatch(ast);
        this.ExtractDscConfigurations(ast);
        this.ExtractDataFlow();

        return (this.nodes, this.hyperedges);
    }

    public HypergraphData ExtractToHypergraph(string sourceCode)
    {
        var (nodes, hyperedges) = this.Extract(sourceCode);
        return new HypergraphData
        {
            Nodes = nodes,
            Hyperedges = hyperedges,
            Metadata = new HypergraphMetadata
            {
                NumFiles = 1,
                NumPowerShellNodes = nodes.Count,
                Files = [this.filePath]
            }
        };
    }

    private void ExtractUsingStatements(System.Management.Automation.Language.Ast ast)
    {
        var usingStatements = ast.FindAll(a => a is UsingStatementAst, searchNestedScriptBlocks: true)
            .Cast<UsingStatementAst>();

        foreach (var usingStmt in usingStatements)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var moduleName = usingStmt.Name?.Value ?? usingStmt.ModuleSpecification?.ToString() ?? "unknown";
            var moduleNode = this.GetOrCreateNode(moduleName, "module", usingStmt.Extent);
            nodes.Add(moduleNode);
            roles[moduleNode.Id] = "imported_module";

            if (usingStmt.Alias != null)
            {
                var aliasNode = this.GetOrCreateNode(usingStmt.Alias.Value, "alias", usingStmt.Extent);
                nodes.Add(aliasNode);
                roles[aliasNode.Id] = "alias";
            }

            this.AddHyperedge(HyperedgeType.ModuleImport, nodes, roles,
                ("using_type", usingStmt.UsingStatementKind.ToString()));
        }
    }

    private void ExtractImportModule(System.Management.Automation.Language.Ast ast)
    {
        var commands = ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Where(c =>
            {
                var name = c.GetCommandName();
                return name != null && name.Equals("Import-Module", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var cmd in commands)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var cmdNode = this.GetOrCreateNode("Import-Module", "cmdlet", cmd.Extent);
            nodes.Add(cmdNode);
            roles[cmdNode.Id] = "cmdlet";

            string? moduleName = null;
            string? moduleAlias = null;
            string? moduleVersion = null;
            string? prefix = null;
            var global = false;
            var force = false;

            string? currentParam = null;
            for (var i = 1; i < cmd.CommandElements.Count; i++)
            {
                var element = cmd.CommandElements[i];

                if (element is CommandParameterAst paramAst)
                {
                    currentParam = paramAst.ParameterName;

                    // Handle switch parameters
                    if (currentParam.Equals("Global", StringComparison.OrdinalIgnoreCase))
                        global = true;
                    else if (currentParam.Equals("Force", StringComparison.OrdinalIgnoreCase))
                        force = true;

                    // Handle inline argument
                    if (paramAst.Argument != null)
                    {
                        var value = GetAstRepr(paramAst.Argument);
                        if (currentParam.Equals("Name", StringComparison.OrdinalIgnoreCase))
                            moduleName = value;
                        else if (currentParam.Equals("Alias", StringComparison.OrdinalIgnoreCase))
                            moduleAlias = value;
                        else if (currentParam.Equals("RequiredVersion", StringComparison.OrdinalIgnoreCase) ||
                                 currentParam.Equals("MinimumVersion", StringComparison.OrdinalIgnoreCase))
                            moduleVersion = value;
                        else if (currentParam.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
                            prefix = value;
                    }
                }
                else
                {
                    var value = GetAstRepr(element);
                    if (currentParam != null)
                    {
                        if (currentParam.Equals("Name", StringComparison.OrdinalIgnoreCase))
                            moduleName = value;
                        else if (currentParam.Equals("Alias", StringComparison.OrdinalIgnoreCase))
                            moduleAlias = value;
                        else if (currentParam.Equals("RequiredVersion", StringComparison.OrdinalIgnoreCase) ||
                                 currentParam.Equals("MinimumVersion", StringComparison.OrdinalIgnoreCase))
                            moduleVersion = value;
                        else if (currentParam.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
                            prefix = value;
                        currentParam = null;
                    }
                    else
                    {
                        // Positional parameter (module name)
                        moduleName = value;
                    }
                }
            }

            if (moduleName != null)
            {
                var moduleNode = this.GetOrCreateNode(moduleName, "module", cmd.Extent);
                nodes.Add(moduleNode);
                roles[moduleNode.Id] = "imported_module";

                // Track for cross-module resolution
                this.loadedModules.Add(moduleName.Trim('\'', '"'));
                if (moduleAlias != null)
                {
                    this.importedModules[moduleAlias.Trim('\'', '"')] = moduleName.Trim('\'', '"');
                }
            }

            if (prefix != null)
            {
                var prefixNode = this.GetOrCreateNode(prefix, "prefix", cmd.Extent);
                nodes.Add(prefixNode);
                roles[prefixNode.Id] = "command_prefix";
            }

            this.AddHyperedge(HyperedgeType.ModuleImport, nodes, roles,
                ("module_name", moduleName),
                ("alias", moduleAlias),
                ("version", moduleVersion),
                ("prefix", prefix),
                ("is_global", global),
                ("is_force", force));
        }
    }

    private void ExtractModuleQualifiedCalls(System.Management.Automation.Language.Ast ast)
    {
        // Find module-qualified function calls: ModuleName\FunctionName
        var commands = ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Where(c =>
            {
                var name = c.GetCommandName();
                return name != null && name.Contains('\\');
            });

        foreach (var cmd in commands)
        {
            var fullName = cmd.GetCommandName()!;
            var parts = fullName.Split('\\', 2);
            var moduleName = parts[0];
            var functionName = parts[1];

            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            // Module node
            var moduleNode = this.GetOrCreateNode(moduleName, "module", cmd.Extent);
            nodes.Add(moduleNode);
            roles[moduleNode.Id] = "source_module";

            // Function node
            var funcNode = this.GetOrCreateNode(functionName, "function_ref", cmd.Extent);
            nodes.Add(funcNode);
            roles[funcNode.Id] = "called_function";

            // Arguments
            string? currentParamName = null;
            var positionalIndex = 0;

            for (var i = 1; i < cmd.CommandElements.Count; i++)
            {
                var element = cmd.CommandElements[i];

                if (element is CommandParameterAst paramAst)
                {
                    currentParamName = paramAst.ParameterName;
                    var paramNode = this.GetOrCreateNode(currentParamName, "param_name", paramAst.Extent);
                    nodes.Add(paramNode);
                    roles[paramNode.Id] = $"param_{currentParamName}";

                    if (paramAst.Argument != null)
                    {
                        var argRepr = GetAstRepr(paramAst.Argument);
                        var argNode = this.GetOrCreateNode(argRepr, "param_value", paramAst.Argument.Extent);
                        nodes.Add(argNode);
                        roles[argNode.Id] = $"value_{currentParamName}";
                        currentParamName = null;
                    }
                }
                else
                {
                    var argRepr = GetAstRepr(element);
                    var argNode = this.GetOrCreateNode(argRepr, "param_value", element.Extent);
                    nodes.Add(argNode);

                    if (currentParamName != null)
                    {
                        roles[argNode.Id] = $"value_{currentParamName}";
                        currentParamName = null;
                    }
                    else
                    {
                        roles[argNode.Id] = $"positional_{positionalIndex++}";
                    }
                }
            }

            this.AddHyperedge(HyperedgeType.ModuleFunctionCall, nodes, roles,
                ("module_name", moduleName),
                ("function_name", functionName),
                ("is_loaded", this.loadedModules.Contains(moduleName)));
        }
    }

    private void ExtractRemoteExecution(System.Management.Automation.Language.Ast ast)
    {
        var commands = ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Where(c =>
            {
                var name = c.GetCommandName();
                return name != null && remoteExecutionCmdlets.Contains(name);
            });

        foreach (var cmd in commands)
        {
            var cmdName = cmd.GetCommandName()!;
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var cmdNode = this.GetOrCreateNode(cmdName, "remote_cmdlet", cmd.Extent);
            nodes.Add(cmdNode);
            roles[cmdNode.Id] = "remote_cmdlet";

            string? computerName = null;
            string? session = null;
            string? credential = null;
            string? scriptBlock = null;
            string? filePath = null;
            string? configurationName = null;
            var useSSL = false;
            int? port = null;
            string? authentication = null;
            var asJob = false;

            string? currentParam = null;
            for (var i = 1; i < cmd.CommandElements.Count; i++)
            {
                var element = cmd.CommandElements[i];

                if (element is CommandParameterAst paramAst)
                {
                    currentParam = paramAst.ParameterName;

                    // Handle switch parameters
                    if (currentParam.Equals("UseSSL", StringComparison.OrdinalIgnoreCase))
                        useSSL = true;
                    else if (currentParam.Equals("AsJob", StringComparison.OrdinalIgnoreCase))
                        asJob = true;

                    // Handle inline argument
                    if (paramAst.Argument != null)
                    {
                        ProcessRemoteParameter(currentParam, paramAst.Argument,
                            ref computerName, ref session, ref credential, ref scriptBlock,
                            ref filePath, ref configurationName, ref port, ref authentication);
                    }
                }
                else
                {
                    if (currentParam != null)
                    {
                        ProcessRemoteParameter(currentParam, element,
                            ref computerName, ref session, ref credential, ref scriptBlock,
                            ref filePath, ref configurationName, ref port, ref authentication);
                        currentParam = null;
                    }
                    else if (element is ScriptBlockExpressionAst scriptBlockAst)
                    {
                        // Positional script block
                        scriptBlock = GetAstRepr(scriptBlockAst);
                    }
                }
            }

            // Add computer name node(s)
            if (computerName != null)
            {
                // Could be a single value or array
                var computers = computerName.Trim('\'', '"', '@', '(', ')').Split(',');
                for (var i = 0; i < computers.Length; i++)
                {
                    var computer = computers[i].Trim().Trim('\'', '"');
                    if (!string.IsNullOrEmpty(computer))
                    {
                        var computerNode = this.GetOrCreateNode(computer, "remote_computer", cmd.Extent);
                        nodes.Add(computerNode);
                        roles[computerNode.Id] = computers.Length > 1 ? $"target_computer_{i}" : "target_computer";
                    }
                }
            }

            // Add session node
            if (session != null)
            {
                var sessionNode = this.GetOrCreateNode(session, "ps_session", cmd.Extent);
                nodes.Add(sessionNode);
                roles[sessionNode.Id] = "session";
            }

            // Add credential node
            if (credential != null)
            {
                var credNode = this.GetOrCreateNode(credential, "credential", cmd.Extent);
                nodes.Add(credNode);
                roles[credNode.Id] = "credential";
            }

            // Add script block node
            if (scriptBlock != null)
            {
                var scriptNode = this.GetOrCreateNode(scriptBlock, "remote_script", cmd.Extent);
                nodes.Add(scriptNode);
                roles[scriptNode.Id] = "script_block";
            }

            // Add file path node
            if (filePath != null)
            {
                var fileNode = this.GetOrCreateNode(filePath, "script_file", cmd.Extent);
                nodes.Add(fileNode);
                roles[fileNode.Id] = "script_file";
            }

            this.AddHyperedge(HyperedgeType.RemoteExecution, nodes, roles,
                ("cmdlet", cmdName),
                ("computer_name", computerName),
                ("session", session),
                ("credential", credential),
                ("use_ssl", useSSL),
                ("port", port),
                ("authentication", authentication),
                ("configuration_name", configurationName),
                ("as_job", asJob),
                ("has_script_block", scriptBlock != null),
                ("has_script_file", filePath != null));
        }
    }

    private static void ProcessRemoteParameter(string paramName, System.Management.Automation.Language.Ast valueAst,
        ref string? computerName, ref string? session, ref string? credential, ref string? scriptBlock,
        ref string? filePath, ref string? configurationName, ref int? port, ref string? authentication)
    {
        var value = GetAstRepr(valueAst);

        if (paramName.Equals("ComputerName", StringComparison.OrdinalIgnoreCase) ||
            paramName.Equals("CN", StringComparison.OrdinalIgnoreCase))
            computerName = value;
        else if (paramName.Equals("Session", StringComparison.OrdinalIgnoreCase))
            session = value;
        else if (paramName.Equals("Credential", StringComparison.OrdinalIgnoreCase))
            credential = value;
        else if (paramName.Equals("ScriptBlock", StringComparison.OrdinalIgnoreCase))
            scriptBlock = value;
        else if (paramName.Equals("FilePath", StringComparison.OrdinalIgnoreCase))
            filePath = value;
        else if (paramName.Equals("ConfigurationName", StringComparison.OrdinalIgnoreCase))
            configurationName = value;
        else if (paramName.Equals("Port", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(value, out var portVal))
            port = portVal;
        else if (paramName.Equals("Authentication", StringComparison.OrdinalIgnoreCase))
            authentication = value;
    }

    private void ExtractFunctions(System.Management.Automation.Language.Ast ast)
    {
        var functions = ast.FindAll(a => a is FunctionDefinitionAst, searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>();

        foreach (var func in functions)
        {
            this.ExtractFunctionDefinition(func);
        }
    }

    private void ExtractFunctionDefinition(FunctionDefinitionAst func)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Function node
        var funcNode = this.GetOrCreateNode(func.Name, "function", func.Extent);
        nodes.Add(funcNode);
        roles[funcNode.Id] = "function";

        // Parameters from param block
        var paramBlock = func.Body.ParamBlock;
        if (paramBlock != null)
        {
            for (var i = 0; i < paramBlock.Parameters.Count; i++)
            {
                var param = paramBlock.Parameters[i];
                var paramName = param.Name.VariablePath.UserPath;
                var paramNode = this.GetOrCreateNode(paramName, "parameter", param.Extent);
                nodes.Add(paramNode);
                roles[paramNode.Id] = $"param_{i}";

                // Track variable definition
                this.TrackVariableDef(paramName, $"{this.CurrentScope}.{func.Name}");

                // Parameter type if specified
                if (param.StaticType != null && param.StaticType != typeof(object))
                {
                    var typeNode = this.GetOrCreateNode(param.StaticType.Name, "type", param.Extent);
                    nodes.Add(typeNode);
                    roles[typeNode.Id] = $"param_type_{i}";
                }

                // Check for parameter attributes
                foreach (var attr in param.Attributes)
                {
                    if (attr is AttributeAst attrAst)
                    {
                        var attrName = attrAst.TypeName.Name;
                        if (attrName == "Parameter" || attrName == "ValidateSet" || attrName == "ValidateNotNullOrEmpty")
                        {
                            var attrNode = this.GetOrCreateNode(attrName, "attribute", attr.Extent);
                            nodes.Add(attrNode);
                            roles[attrNode.Id] = $"param_{i}_attr";
                        }
                    }
                }
            }
        }

        // CmdletBinding attribute
        if (paramBlock?.Attributes.Any(a => a is AttributeAst attr && attr.TypeName.Name == "CmdletBinding") == true)
        {
            var cmdletBindingNode = this.GetOrCreateNode("CmdletBinding", "attribute", func.Extent);
            nodes.Add(cmdletBindingNode);
            roles[cmdletBindingNode.Id] = "cmdlet_binding";
        }

        // OutputType attribute
        var outputTypes = func.Body.ParamBlock?.Attributes
            .OfType<AttributeAst>()
            .Where(a => a.TypeName.Name == "OutputType")
            .ToList() ?? [];

        foreach (var outputType in outputTypes)
        {
            if (outputType.PositionalArguments.Count > 0)
            {
                var typeExpr = outputType.PositionalArguments[0];
                var typeName = typeExpr is TypeExpressionAst typeAst
                    ? typeAst.TypeName.Name
                    : typeExpr.ToString();
                var outputNode = this.GetOrCreateNode(typeName, "output_type", outputType.Extent);
                nodes.Add(outputNode);
                roles[outputNode.Id] = "output_type";
            }
        }

        this.AddHyperedge(HyperedgeType.FunctionDefinition, nodes, roles,
            ("is_filter", func.IsFilter),
            ("is_workflow", func.IsWorkflow));

        // Process function body
        this.scopeStack.Push(func.Name);
        this.ProcessScriptBlock(func.Body);
        this.scopeStack.Pop();
    }

    private void ExtractClasses(System.Management.Automation.Language.Ast ast)
    {
        var classes = ast.FindAll(a => a is TypeDefinitionAst, searchNestedScriptBlocks: true)
            .Cast<TypeDefinitionAst>();

        foreach (var classAst in classes)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var classNode = this.GetOrCreateNode(classAst.Name, classAst.IsClass ? "class" : "enum", classAst.Extent);
            nodes.Add(classNode);
            roles[classNode.Id] = classAst.IsClass ? "class" : "enum";

            // Base types
            if (classAst.BaseTypes != null)
            {
                for (var i = 0; i < classAst.BaseTypes.Count; i++)
                {
                    var baseType = classAst.BaseTypes[i];
                    var baseNode = this.GetOrCreateNode(baseType.TypeName.Name, "base_type", baseType.Extent);
                    nodes.Add(baseNode);
                    roles[baseNode.Id] = $"base_{i}";
                }
            }

            this.AddHyperedge(HyperedgeType.ClassDefinition, nodes, roles);

            // Process class members
            this.scopeStack.Push(classAst.Name);
            foreach (var member in classAst.Members)
            {
                if (member is FunctionMemberAst method)
                {
                    this.ExtractClassMethod(method);
                }
                else if (member is PropertyMemberAst property)
                {
                    this.ExtractClassProperty(property);
                }
            }
            this.scopeStack.Pop();
        }
    }

    private void ExtractClassMethod(FunctionMemberAst method)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var methodNode = this.GetOrCreateNode(method.Name, "method", method.Extent);
        nodes.Add(methodNode);
        roles[methodNode.Id] = "method";

        // Return type
        if (method.ReturnType != null)
        {
            var returnNode = this.GetOrCreateNode(method.ReturnType.TypeName.Name, "type", method.ReturnType.Extent);
            nodes.Add(returnNode);
            roles[returnNode.Id] = "return_type";
        }

        // Parameters
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var param = method.Parameters[i];
            var paramNode = this.GetOrCreateNode(param.Name.VariablePath.UserPath, "parameter", param.Extent);
            nodes.Add(paramNode);
            roles[paramNode.Id] = $"param_{i}";
        }

        this.AddHyperedge(HyperedgeType.MethodDefinition, nodes, roles,
            ("is_static", method.IsStatic),
            ("is_constructor", method.IsConstructor));
    }

    private void ExtractClassProperty(PropertyMemberAst property)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var propNode = this.GetOrCreateNode(property.Name, "property", property.Extent);
        nodes.Add(propNode);
        roles[propNode.Id] = "property";

        if (property.PropertyType != null)
        {
            var typeNode = this.GetOrCreateNode(property.PropertyType.TypeName.Name, "type", property.PropertyType.Extent);
            nodes.Add(typeNode);
            roles[typeNode.Id] = "property_type";
        }

        this.AddHyperedge(HyperedgeType.PropertyDefinition, nodes, roles);
    }

    private void ExtractCommands(System.Management.Automation.Language.Ast ast)
    {
        var commands = ast.FindAll(a => a is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>();

        foreach (var cmd in commands)
        {
            this.ExtractCommandCall(cmd);
        }
    }

    private void ExtractCommandCall(CommandAst cmd)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Command name
        var cmdName = cmd.GetCommandName() ?? cmd.CommandElements.FirstOrDefault()?.ToString() ?? "unknown";
        var cmdNode = this.GetOrCreateNode(cmdName, "cmdlet", cmd.Extent);
        nodes.Add(cmdNode);
        roles[cmdNode.Id] = "cmdlet";

        // Parameters and arguments - ALL in one hyperedge
        string? currentParamName = null;
        var positionalIndex = 0;

        for (var i = 1; i < cmd.CommandElements.Count; i++)
        {
            var element = cmd.CommandElements[i];

            if (element is CommandParameterAst paramAst)
            {
                currentParamName = paramAst.ParameterName;
                var paramNode = this.GetOrCreateNode(currentParamName, "param_name", paramAst.Extent);
                nodes.Add(paramNode);
                roles[paramNode.Id] = $"param_{currentParamName}";

                // Inline argument (e.g., -Name:Value)
                if (paramAst.Argument != null)
                {
                    var argRepr = GetAstRepr(paramAst.Argument);
                    var argNode = this.GetOrCreateNode(argRepr, "param_value", paramAst.Argument.Extent);
                    nodes.Add(argNode);
                    roles[argNode.Id] = $"value_{currentParamName}";
                    this.ProcessExpression(paramAst.Argument);
                }
            }
            else
            {
                var argRepr = GetAstRepr(element);
                var argNode = this.GetOrCreateNode(argRepr, "param_value", element.Extent);
                nodes.Add(argNode);

                if (currentParamName != null)
                {
                    roles[argNode.Id] = $"value_{currentParamName}";
                    currentParamName = null;
                }
                else
                {
                    roles[argNode.Id] = $"positional_{positionalIndex++}";
                }

                if (element is ExpressionAst exprAst)
                {
                    this.ProcessExpression(exprAst);
                }
            }
        }

        if (nodes.Count > 0)
        {
            // Determine if this is a known cmdlet pattern
            var isCmdlet = cmdName.Contains('-') && char.IsUpper(cmdName[0]);
            this.AddHyperedge(HyperedgeType.CmdletCall, nodes, roles,
                ("is_cmdlet", isCmdlet));
        }
    }

    private void ExtractPipelines(System.Management.Automation.Language.Ast ast)
    {
        var pipelines = ast.FindAll(a => a is PipelineAst, searchNestedScriptBlocks: true)
            .Cast<PipelineAst>()
            .Where(p => p.PipelineElements.Count > 1);

        foreach (var pipeline in pipelines)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            for (var i = 0; i < pipeline.PipelineElements.Count; i++)
            {
                var element = pipeline.PipelineElements[i];
                string cmdName;

                if (element is CommandAst cmdAst)
                {
                    cmdName = cmdAst.GetCommandName() ?? element.ToString();
                }
                else
                {
                    cmdName = GetAstRepr(element);
                }

                var cmdNode = this.GetOrCreateNode(cmdName, "pipeline_stage", element.Extent);
                nodes.Add(cmdNode);
                roles[cmdNode.Id] = $"stage_{i}";
            }

            if (nodes.Count > 1)
            {
                this.AddHyperedge(HyperedgeType.Pipeline, nodes, roles,
                    ("pipeline_length", nodes.Count));
            }
        }
    }

    private void ExtractAssignments(System.Management.Automation.Language.Ast ast)
    {
        var assignments = ast.FindAll(a => a is AssignmentStatementAst, searchNestedScriptBlocks: true)
            .Cast<AssignmentStatementAst>();

        foreach (var assignment in assignments)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            // Target variable
            var targetRepr = GetAstRepr(assignment.Left);
            var targetNode = this.GetOrCreateNode(targetRepr, "variable", assignment.Left.Extent);
            nodes.Add(targetNode);
            roles[targetNode.Id] = "target";

            // Track variable definition
            if (assignment.Left is VariableExpressionAst varExpr)
            {
                var line = assignment.Extent.StartLineNumber;
                this.TrackVariableDef(varExpr.VariablePath.UserPath, $"{this.CurrentScope}:L{line}");
            }

            // Value
            var valueRepr = GetAstRepr(assignment.Right);
            var valueNode = this.GetOrCreateNode(valueRepr, "expression", assignment.Right.Extent);
            nodes.Add(valueNode);
            roles[valueNode.Id] = "value";

            this.AddHyperedge(HyperedgeType.Assignment, nodes, roles,
                ("operator", assignment.Operator.ToString()));

            this.ProcessStatement(assignment.Right);
        }
    }

    private void ExtractLoops(System.Management.Automation.Language.Ast ast)
    {
        // ForEach loops
        var foreachLoops = ast.FindAll(a => a is ForEachStatementAst, searchNestedScriptBlocks: true)
            .Cast<ForEachStatementAst>();

        foreach (var loop in foreachLoops)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var iteratorNode = this.GetOrCreateNode(loop.Variable.VariablePath.UserPath, "loop_variable", loop.Variable.Extent);
            nodes.Add(iteratorNode);
            roles[iteratorNode.Id] = "iterator";

            var collectionRepr = GetAstRepr(loop.Condition);
            var collectionNode = this.GetOrCreateNode(collectionRepr, "collection", loop.Condition.Extent);
            nodes.Add(collectionNode);
            roles[collectionNode.Id] = "iterable";

            this.TrackVariableDef(loop.Variable.VariablePath.UserPath, $"{this.CurrentScope}:L{loop.Extent.StartLineNumber}");

            this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
                ("loop_type", "foreach"));
        }

        // For loops
        var forLoops = ast.FindAll(a => a is ForStatementAst, searchNestedScriptBlocks: true)
            .Cast<ForStatementAst>();

        foreach (var loop in forLoops)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            if (loop.Condition != null)
            {
                var conditionRepr = GetAstRepr(loop.Condition);
                var conditionNode = this.GetOrCreateNode(conditionRepr, "condition", loop.Condition.Extent);
                nodes.Add(conditionNode);
                roles[conditionNode.Id] = "condition";
            }

            this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
                ("loop_type", "for"));
        }

        // While loops
        var whileLoops = ast.FindAll(a => a is WhileStatementAst, searchNestedScriptBlocks: true)
            .Cast<WhileStatementAst>();

        foreach (var loop in whileLoops)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var conditionRepr = GetAstRepr(loop.Condition);
            var conditionNode = this.GetOrCreateNode(conditionRepr, "condition", loop.Condition.Extent);
            nodes.Add(conditionNode);
            roles[conditionNode.Id] = "condition";

            this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
                ("loop_type", "while"));
        }
    }

    private void ExtractTryCatch(System.Management.Automation.Language.Ast ast)
    {
        var tryStatements = ast.FindAll(a => a is TryStatementAst, searchNestedScriptBlocks: true)
            .Cast<TryStatementAst>();

        foreach (var tryStmt in tryStatements)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var tryNode = this.GetOrCreateNode($"try_L{tryStmt.Extent.StartLineNumber}", "try_block", tryStmt.Extent);
            nodes.Add(tryNode);
            roles[tryNode.Id] = "try_block";

            for (var i = 0; i < tryStmt.CatchClauses.Count; i++)
            {
                var catchClause = tryStmt.CatchClauses[i];
                var exceptionTypes = catchClause.CatchTypes.Select(t => t.TypeName.Name).ToList();
                var exceptionType = exceptionTypes.Count > 0 ? string.Join(", ", exceptionTypes) : "all";

                var catchNode = this.GetOrCreateNode(exceptionType, "exception_handler", catchClause.Extent);
                nodes.Add(catchNode);
                roles[catchNode.Id] = $"catch_{i}";
            }

            if (tryStmt.Finally != null)
            {
                var finallyNode = this.GetOrCreateNode($"finally_L{tryStmt.Finally.Extent.StartLineNumber}", "finally_block", tryStmt.Finally.Extent);
                nodes.Add(finallyNode);
                roles[finallyNode.Id] = "finally";
            }

            this.AddHyperedge(HyperedgeType.ExceptionHandling, nodes, roles);
        }
    }

    private void ExtractDscConfigurations(System.Management.Automation.Language.Ast ast)
    {
        var configurations = ast.FindAll(a => a is ConfigurationDefinitionAst, searchNestedScriptBlocks: true)
            .Cast<ConfigurationDefinitionAst>();

        foreach (var config in configurations)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var configName = config.InstanceName?.ToString() ?? "Configuration";
            var configNode = this.GetOrCreateNode(configName, "dsc_configuration", config.Extent);
            nodes.Add(configNode);
            roles[configNode.Id] = "configuration";

            this.AddHyperedge(HyperedgeType.DscConfiguration, nodes, roles);

            // Extract DSC resources within the configuration
            this.scopeStack.Push(configName);
            if (config.Body is ScriptBlockExpressionAst scriptBlockExpr)
            {
                this.ExtractDscResources(scriptBlockExpr.ScriptBlock);
            }
            this.scopeStack.Pop();
        }
    }

    private void ExtractDscResources(ScriptBlockAst scriptBlock)
    {
        // DSC resources appear as dynamic keyword statements
        var dynamicKeywords = scriptBlock.FindAll(a => a is DynamicKeywordStatementAst, searchNestedScriptBlocks: true)
            .Cast<DynamicKeywordStatementAst>();

        foreach (var resource in dynamicKeywords)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var resourceType = resource.CommandElements.FirstOrDefault()?.ToString() ?? "Resource";
            var resourceNode = this.GetOrCreateNode(resourceType, "dsc_resource_type", resource.Extent);
            nodes.Add(resourceNode);
            roles[resourceNode.Id] = "resource_type";

            // Resource instance name
            if (resource.CommandElements.Count > 1)
            {
                var instanceName = resource.CommandElements[1].ToString();
                var instanceNode = this.GetOrCreateNode(instanceName, "dsc_resource_instance", resource.CommandElements[1].Extent);
                nodes.Add(instanceNode);
                roles[instanceNode.Id] = "instance_name";
            }

            this.AddHyperedge(HyperedgeType.DscResource, nodes, roles);
        }
    }

    private void ProcessScriptBlock(ScriptBlockAst scriptBlock)
    {
        if (scriptBlock.BeginBlock != null)
        {
            this.ProcessStatements(scriptBlock.BeginBlock.Statements);
        }
        if (scriptBlock.ProcessBlock != null)
        {
            this.ProcessStatements(scriptBlock.ProcessBlock.Statements);
        }
        if (scriptBlock.EndBlock != null)
        {
            this.ProcessStatements(scriptBlock.EndBlock.Statements);
        }
    }

    private void ProcessStatements(IEnumerable<StatementAst> statements)
    {
        foreach (var statement in statements)
        {
            this.ProcessStatement(statement);
        }
    }

    private void ProcessStatement(StatementAst statement)
    {
        switch (statement)
        {
            case PipelineAst pipeline:
                foreach (var element in pipeline.PipelineElements)
                {
                    if (element is CommandExpressionAst cmdExpr)
                    {
                        this.ProcessExpression(cmdExpr.Expression);
                    }
                }
                break;
            case IfStatementAst ifStmt:
                foreach (var clause in ifStmt.Clauses)
                {
                    this.ProcessPipeline(clause.Item1);
                    this.ProcessStatements(clause.Item2.Statements);
                }
                if (ifStmt.ElseClause != null)
                {
                    this.ProcessStatements(ifStmt.ElseClause.Statements);
                }
                break;
            case ForEachStatementAst forEach:
                this.ProcessPipeline(forEach.Condition);
                this.ProcessStatements(forEach.Body.Statements);
                break;
            case TryStatementAst tryStmt:
                this.ProcessStatements(tryStmt.Body.Statements);
                foreach (var catchClause in tryStmt.CatchClauses)
                {
                    this.ProcessStatements(catchClause.Body.Statements);
                }
                if (tryStmt.Finally != null)
                {
                    this.ProcessStatements(tryStmt.Finally.Statements);
                }
                break;
        }
    }

    private void ProcessPipeline(PipelineBaseAst pipeline)
    {
        if (pipeline is PipelineAst p)
        {
            foreach (var element in p.PipelineElements)
            {
                if (element is CommandExpressionAst cmdExpr)
                {
                    this.ProcessExpression(cmdExpr.Expression);
                }
            }
        }
    }

    private void ProcessExpression(ExpressionAst expression)
    {
        switch (expression)
        {
            case VariableExpressionAst varExpr:
                var line = varExpr.Extent.StartLineNumber;
                this.TrackVariableUse(varExpr.VariablePath.UserPath, $"{this.CurrentScope}:L{line}");
                break;
            case InvokeMemberExpressionAst invokeExpr:
                this.ProcessExpression(invokeExpr.Expression);
                foreach (var arg in invokeExpr.Arguments ?? Enumerable.Empty<ExpressionAst>())
                {
                    this.ProcessExpression(arg);
                }
                break;
            case MemberExpressionAst memberExpr:
                this.ProcessExpression(memberExpr.Expression);
                break;
            case BinaryExpressionAst binaryExpr:
                this.ProcessExpression(binaryExpr.Left);
                this.ProcessExpression(binaryExpr.Right);
                break;
            case ArrayExpressionAst arrayExpr:
                this.ProcessStatements(arrayExpr.SubExpression.Statements);
                break;
        }
    }

    private void ExtractDataFlow()
    {
        foreach (var (varName, defLocations) in this.variableDefs)
        {
            if (!this.variableUses.TryGetValue(varName, out var useLocations) || useLocations.Count == 0)
                continue;

            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            for (var i = 0; i < defLocations.Count; i++)
            {
                var defNode = this.GetOrCreateNode($"${varName}@{defLocations[i]}", "definition", null);
                nodes.Add(defNode);
                roles[defNode.Id] = $"def_{i}";
            }

            for (var i = 0; i < useLocations.Count; i++)
            {
                var useNode = this.GetOrCreateNode($"${varName}@{useLocations[i]}", "use", null);
                nodes.Add(useNode);
                roles[useNode.Id] = $"use_{i}";
            }

            if (nodes.Count > 1)
            {
                this.AddHyperedge(HyperedgeType.DataFlow, nodes, roles,
                    ("variable", varName));
            }
        }
    }

    private HypergraphNode GetOrCreateNode(string name, string nodeType, IScriptExtent? extent)
    {
        var line = extent?.StartLineNumber ?? 0;
        var nodeId = this.GenerateNodeId(name, nodeType, line);

        if (!this.nodes.TryGetValue(nodeId, out var node))
        {
            node = new HypergraphNode
            {
                Id = nodeId,
                Name = name,
                NodeType = nodeType,
                Language = "powershell",
                FilePath = this.filePath,
                Scope = this.CurrentScope,
                LineStart = line,
                LineEnd = extent?.EndLineNumber ?? line
            };
            this.nodes[nodeId] = node;
        }

        return node;
    }

    private string GenerateNodeId(string name, string nodeType, int line)
    {
        var raw = $"{nodeType}:{this.CurrentScope}.{name}:{line}";
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private void AddHyperedge(HyperedgeType edgeType, List<HypergraphNode> nodes, Dictionary<string, string> roles, params (string Key, object? Value)[] metadata)
    {
        if (nodes.Count == 0) return;

        var edge = new Hyperedge
        {
            Id = $"he_{++this.edgeCounter:D4}",
            EdgeType = edgeType,
            NodeIds = [.. nodes.Select(n => n.Id)],
            Roles = roles,
            Languages = ["powershell"],
            Metadata = metadata.ToDictionary(m => m.Key, m => m.Value)
        };
        this.hyperedges.Add(edge);
    }

    private void TrackVariableDef(string name, string location)
    {
        if (!this.variableDefs.ContainsKey(name))
            this.variableDefs[name] = [];
        this.variableDefs[name].Add(location);
    }

    private void TrackVariableUse(string name, string location)
    {
        if (!this.variableUses.ContainsKey(name))
            this.variableUses[name] = [];
        this.variableUses[name].Add(location);
    }

    private static string GetAstRepr(System.Management.Automation.Language.Ast ast)
    {
        var text = ast.ToString();
        return text.Length > 50 ? text[..50] + "..." : text;
    }
}