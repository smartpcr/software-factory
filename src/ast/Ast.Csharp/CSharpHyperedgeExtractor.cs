// -----------------------------------------------------------------------
// <copyright file="CSharpHyperedgeExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Csharp;
using System.Security.Cryptography;
using System.Text;
using Ast.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Extracts hyperedges from C# source code using Roslyn semantic analysis.
///
/// Key insight: Traditional graphs lose context when representing code.
/// For example, a method call f(a, b, c) creates edges: f-a, f-b, f-c
/// But this loses the fact that a, b, c are ALL arguments to the SAME call.
///
/// Hyperedges preserve this: {f, a, b, c} with roles {callee, arg_0, arg_1, arg_2}
/// </summary>
public class CSharpHyperedgeExtractor
{
    private readonly Dictionary<string, HypergraphNode> nodes = [];
    private readonly List<Hyperedge> hyperedges = [];
    private readonly string filePath;
    private readonly Stack<string> scopeStack = new();
    private int edgeCounter;

    // Track for data flow analysis
    private readonly Dictionary<string, List<string>> variableDefs = [];
    private readonly Dictionary<string, List<string>> variableUses = [];

    // DI registration method names
    private static readonly HashSet<string> DiRegistrationMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddSingleton", "AddScoped", "AddTransient",
        "AddHostedService", "AddHttpClient",
        "TryAddSingleton", "TryAddScoped", "TryAddTransient",
        "AddDbContext", "AddDbContextPool", "AddDbContextFactory"
    };

    // HTTP method attributes for API endpoints
    private static readonly HashSet<string> HttpMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "Route", "ApiController"
    };

    // Logging method names
    private static readonly HashSet<string> LoggingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log"
    };

    // File system methods
    private static readonly HashSet<string> FileSystemMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadAllText", "ReadAllLines", "ReadAllBytes", "WriteAllText", "WriteAllLines", "WriteAllBytes",
        "OpenRead", "OpenWrite", "Create", "Delete", "Exists", "Copy", "Move",
        "CreateDirectory", "GetFiles", "GetDirectories", "EnumerateFiles"
    };

    // Track architectural elements
    private bool isController;
    private string? controllerRoute;
    private readonly List<string> registeredServices = [];

    public CSharpHyperedgeExtractor(string filePath)
    {
        this.filePath = filePath;
        this.scopeStack.Push(Path.GetFileNameWithoutExtension(filePath));
    }

    private string CurrentScope => string.Join(".", this.scopeStack.Reverse());

    public (Dictionary<string, HypergraphNode> Nodes, List<Hyperedge> Hyperedges) Extract(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        // Check for top-level statements (minimal hosting / C# 9+)
        this.ExtractTopLevelStatements(root);

        // Extract namespace
        var namespaceDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDecl != null)
        {
            this.scopeStack.Push(namespaceDecl.Name.ToString());
        }

        // Process using directives
        foreach (var usingDirective in root.Usings)
        {
            this.ExtractUsingDirective(usingDirective);
        }

        // Process all declarations
        foreach (var member in root.Members)
        {
            this.ProcessMember(member);
        }

        // Extract data flow hyperedges
        this.ExtractDataFlowHyperedges();

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
                NumCSharpNodes = nodes.Count,
                Files = [this.filePath]
            }
        };
    }

    private void ProcessMember(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax ns:
                this.ProcessNamespace(ns);
                break;
            case ClassDeclarationSyntax classDecl:
                this.ExtractClassDefinition(classDecl);
                break;
            case InterfaceDeclarationSyntax interfaceDecl:
                this.ExtractInterfaceDefinition(interfaceDecl);
                break;
            case StructDeclarationSyntax structDecl:
                this.ExtractStructDefinition(structDecl);
                break;
            case RecordDeclarationSyntax recordDecl:
                this.ExtractRecordDefinition(recordDecl);
                break;
            case EnumDeclarationSyntax enumDecl:
                this.ExtractEnumDefinition(enumDecl);
                break;
        }
    }

    private void ProcessNamespace(BaseNamespaceDeclarationSyntax ns)
    {
        this.scopeStack.Push(ns.Name.ToString());
        foreach (var member in ns.Members)
        {
            this.ProcessMember(member);
        }
        this.scopeStack.Pop();
    }

    private void ExtractUsingDirective(UsingDirectiveSyntax usingDirective)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var namespaceName = usingDirective.Name?.ToString() ?? usingDirective.NamespaceOrType.ToString();
        var nsNode = this.GetOrCreateNode(namespaceName, "namespace", usingDirective.GetLocation());
        nodes.Add(nsNode);
        roles[nsNode.Id] = "imported_namespace";

        if (usingDirective.Alias != null)
        {
            var aliasNode = this.GetOrCreateNode(usingDirective.Alias.Name.ToString(), "alias", usingDirective.GetLocation());
            nodes.Add(aliasNode);
            roles[aliasNode.Id] = "alias";
        }

        this.AddHyperedge(HyperedgeType.UsingDirective, nodes, roles,
            ("is_static", usingDirective.StaticKeyword != default));
    }

    private void ExtractClassDefinition(ClassDeclarationSyntax classDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Class node
        var classNode = this.GetOrCreateNode(classDecl.Identifier.Text, "class", classDecl.GetLocation(),
            modifiers: GetModifiers(classDecl.Modifiers));
        nodes.Add(classNode);
        roles[classNode.Id] = "class";

        // Base types - THIS IS KEY FOR HYPERGRAPHS
        // Captures multi-inheritance (interfaces) in one hyperedge
        if (classDecl.BaseList != null)
        {
            for (int i = 0; i < classDecl.BaseList.Types.Count; i++)
            {
                var baseType = classDecl.BaseList.Types[i];
                var baseTypeName = baseType.Type.ToString();
                var nodeType = baseTypeName.StartsWith("I") && baseTypeName.Length > 1 && char.IsUpper(baseTypeName[1])
                    ? "interface" : "class";
                var baseNode = this.GetOrCreateNode(baseTypeName, nodeType, baseType.GetLocation());
                nodes.Add(baseNode);
                roles[baseNode.Id] = $"base_{i}";
            }
        }

        this.AddHyperedge(HyperedgeType.ClassDefinition, nodes, roles,
            ("modifiers", GetModifiers(classDecl.Modifiers)));

        // Check if this is a controller
        this.isController = false;
        this.controllerRoute = null;

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (attrName == "ApiController" || attrName == "ApiControllerAttribute")
                {
                    this.isController = true;
                }
                else if (attrName == "Route" || attrName == "RouteAttribute")
                {
                    this.isController = true;
                    if (attr.ArgumentList?.Arguments.Count > 0)
                    {
                        this.controllerRoute = attr.ArgumentList.Arguments[0].ToString().Trim('"');
                    }
                }
                else if (attrName == "Controller" || attrName == "ControllerAttribute")
                {
                    this.isController = true;
                }
            }
        }

        // Also check if class name ends with Controller
        if (classDecl.Identifier.Text.EndsWith("Controller"))
        {
            this.isController = true;
        }

        // Check base types for ControllerBase
        if (classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                var baseName = baseType.Type.ToString();
                if (baseName.Contains("Controller") || baseName.Contains("ControllerBase"))
                {
                    this.isController = true;
                    break;
                }
            }
        }

        // Process class members
        this.scopeStack.Push(classDecl.Identifier.Text);

        foreach (var member in classDecl.Members)
        {
            this.ProcessClassMember(member, classDecl.Identifier.Text);
        }

        this.scopeStack.Pop();
        this.isController = false;
        this.controllerRoute = null;
    }

    private void ExtractInterfaceDefinition(InterfaceDeclarationSyntax interfaceDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var interfaceNode = this.GetOrCreateNode(interfaceDecl.Identifier.Text, "interface", interfaceDecl.GetLocation(),
            modifiers: GetModifiers(interfaceDecl.Modifiers));
        nodes.Add(interfaceNode);
        roles[interfaceNode.Id] = "interface";

        if (interfaceDecl.BaseList != null)
        {
            for (int i = 0; i < interfaceDecl.BaseList.Types.Count; i++)
            {
                var baseType = interfaceDecl.BaseList.Types[i];
                var baseNode = this.GetOrCreateNode(baseType.Type.ToString(), "interface", baseType.GetLocation());
                nodes.Add(baseNode);
                roles[baseNode.Id] = $"extends_{i}";
            }
        }

        this.AddHyperedge(HyperedgeType.InterfaceDefinition, nodes, roles);

        this.scopeStack.Push(interfaceDecl.Identifier.Text);
        foreach (var member in interfaceDecl.Members)
        {
            this.ProcessClassMember(member, interfaceDecl.Identifier.Text);
        }
        this.scopeStack.Pop();
    }

    private void ExtractStructDefinition(StructDeclarationSyntax structDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var structNode = this.GetOrCreateNode(structDecl.Identifier.Text, "struct", structDecl.GetLocation(),
            modifiers: GetModifiers(structDecl.Modifiers));
        nodes.Add(structNode);
        roles[structNode.Id] = "struct";

        if (structDecl.BaseList != null)
        {
            for (int i = 0; i < structDecl.BaseList.Types.Count; i++)
            {
                var baseType = structDecl.BaseList.Types[i];
                var baseNode = this.GetOrCreateNode(baseType.Type.ToString(), "interface", baseType.GetLocation());
                nodes.Add(baseNode);
                roles[baseNode.Id] = $"implements_{i}";
            }
        }

        this.AddHyperedge(HyperedgeType.StructDefinition, nodes, roles);

        this.scopeStack.Push(structDecl.Identifier.Text);
        foreach (var member in structDecl.Members)
        {
            this.ProcessClassMember(member, structDecl.Identifier.Text);
        }
        this.scopeStack.Pop();
    }

    private void ExtractRecordDefinition(RecordDeclarationSyntax recordDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var recordNode = this.GetOrCreateNode(recordDecl.Identifier.Text, "record", recordDecl.GetLocation(),
            modifiers: GetModifiers(recordDecl.Modifiers));
        nodes.Add(recordNode);
        roles[recordNode.Id] = "record";

        // Primary constructor parameters
        if (recordDecl.ParameterList != null)
        {
            for (int i = 0; i < recordDecl.ParameterList.Parameters.Count; i++)
            {
                var param = recordDecl.ParameterList.Parameters[i];
                var paramNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
                nodes.Add(paramNode);
                roles[paramNode.Id] = $"param_{i}";

                if (param.Type != null)
                {
                    var typeNode = this.GetOrCreateNode(param.Type.ToString(), "type", param.Type.GetLocation());
                    nodes.Add(typeNode);
                    roles[typeNode.Id] = $"param_type_{i}";
                }
            }
        }

        this.AddHyperedge(HyperedgeType.RecordDefinition, nodes, roles);

        this.scopeStack.Push(recordDecl.Identifier.Text);
        foreach (var member in recordDecl.Members)
        {
            this.ProcessClassMember(member, recordDecl.Identifier.Text);
        }
        this.scopeStack.Pop();
    }

    private void ExtractEnumDefinition(EnumDeclarationSyntax enumDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var enumNode = this.GetOrCreateNode(enumDecl.Identifier.Text, "enum", enumDecl.GetLocation());
        nodes.Add(enumNode);
        roles[enumNode.Id] = "enum";

        foreach (var member in enumDecl.Members)
        {
            var memberNode = this.GetOrCreateNode(member.Identifier.Text, "enum_member", member.GetLocation());
            nodes.Add(memberNode);
            roles[memberNode.Id] = $"member_{enumDecl.Members.IndexOf(member)}";
        }

        this.AddHyperedge(HyperedgeType.ClassDefinition, nodes, roles, ("is_enum", true));
    }

    private void ProcessClassMember(MemberDeclarationSyntax member, string className)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                this.ExtractMethodDefinition(method);
                break;
            case ConstructorDeclarationSyntax ctor:
                this.ExtractConstructorDefinition(ctor, className);
                break;
            case PropertyDeclarationSyntax property:
                this.ExtractPropertyDefinition(property);
                break;
            case FieldDeclarationSyntax field:
                this.ExtractFieldDefinition(field);
                break;
            case EventDeclarationSyntax eventDecl:
                this.ExtractEventDefinition(eventDecl);
                break;
            case ClassDeclarationSyntax nestedClass:
                this.ExtractClassDefinition(nestedClass);
                break;
            case InterfaceDeclarationSyntax nestedInterface:
                this.ExtractInterfaceDefinition(nestedInterface);
                break;
        }
    }

    private void ExtractMethodDefinition(MethodDeclarationSyntax method)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Method node
        var methodNode = this.GetOrCreateNode(method.Identifier.Text, "method", method.GetLocation(),
            modifiers: GetModifiers(method.Modifiers));
        nodes.Add(methodNode);
        roles[methodNode.Id] = "method";

        // Return type
        var returnTypeNode = this.GetOrCreateNode(method.ReturnType.ToString(), "type", method.ReturnType.GetLocation());
        nodes.Add(returnTypeNode);
        roles[returnTypeNode.Id] = "return_type";

        // Parameters - ALL together in one hyperedge
        for (int i = 0; i < method.ParameterList.Parameters.Count; i++)
        {
            var param = method.ParameterList.Parameters[i];
            var paramNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
            nodes.Add(paramNode);
            roles[paramNode.Id] = $"param_{i}";

            if (param.Type != null)
            {
                var typeNode = this.GetOrCreateNode(param.Type.ToString(), "type", param.Type.GetLocation());
                nodes.Add(typeNode);
                roles[typeNode.Id] = $"param_type_{i}";
            }

            // Track variable definition
            this.TrackVariableDef(param.Identifier.Text, $"{this.CurrentScope}.{method.Identifier.Text}");
        }

        // Type parameters (generics)
        if (method.TypeParameterList != null)
        {
            for (int i = 0; i < method.TypeParameterList.Parameters.Count; i++)
            {
                var typeParam = method.TypeParameterList.Parameters[i];
                var typeParamNode = this.GetOrCreateNode(typeParam.Identifier.Text, "type_parameter", typeParam.GetLocation());
                nodes.Add(typeParamNode);
                roles[typeParamNode.Id] = $"type_param_{i}";
            }
        }

        var isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
        this.AddHyperedge(HyperedgeType.MethodDefinition, nodes, roles,
            ("is_async", isAsync),
            ("modifiers", GetModifiers(method.Modifiers)));

        // Check for API endpoint if in a controller
        if (this.isController)
        {
            this.ExtractApiEndpoint(method);
        }

        // Check for entry point (Main method)
        this.ExtractEntryPointIfMain(method);

        // Process method body
        this.scopeStack.Push(method.Identifier.Text);
        if (method.Body != null)
        {
            this.ProcessStatements(method.Body.Statements);
        }
        else if (method.ExpressionBody != null)
        {
            this.ProcessExpression(method.ExpressionBody.Expression);
        }
        this.scopeStack.Pop();
    }

    private void ExtractConstructorDefinition(ConstructorDeclarationSyntax ctor, string className)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var ctorNode = this.GetOrCreateNode($"{className}.ctor", "constructor", ctor.GetLocation(),
            modifiers: GetModifiers(ctor.Modifiers));
        nodes.Add(ctorNode);
        roles[ctorNode.Id] = "constructor";

        // Parameters
        for (int i = 0; i < ctor.ParameterList.Parameters.Count; i++)
        {
            var param = ctor.ParameterList.Parameters[i];
            var paramNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
            nodes.Add(paramNode);
            roles[paramNode.Id] = $"param_{i}";

            if (param.Type != null)
            {
                var typeNode = this.GetOrCreateNode(param.Type.ToString(), "type", param.Type.GetLocation());
                nodes.Add(typeNode);
                roles[typeNode.Id] = $"param_type_{i}";

                // Check for DI pattern (interface parameter assigned to field)
                if (param.Type.ToString().StartsWith("I") && param.Type.ToString().Length > 1)
                {
                    this.ExtractDependencyInjection(param);
                }
            }

            this.TrackVariableDef(param.Identifier.Text, $"{this.CurrentScope}.ctor");
        }

        // Constructor initializer (: base() or : this())
        if (ctor.Initializer != null)
        {
            var initNode = this.GetOrCreateNode(ctor.Initializer.ThisOrBaseKeyword.Text, "chain_call", ctor.Initializer.GetLocation());
            nodes.Add(initNode);
            roles[initNode.Id] = "chain";
        }

        this.AddHyperedge(HyperedgeType.ConstructorDefinition, nodes, roles);

        // Process constructor body
        this.scopeStack.Push("ctor");
        if (ctor.Body != null)
        {
            this.ProcessStatements(ctor.Body.Statements);
        }
        this.scopeStack.Pop();
    }

    private void ExtractPropertyDefinition(PropertyDeclarationSyntax property)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var propNode = this.GetOrCreateNode(property.Identifier.Text, "property", property.GetLocation(),
            modifiers: GetModifiers(property.Modifiers));
        nodes.Add(propNode);
        roles[propNode.Id] = "property";

        var typeNode = this.GetOrCreateNode(property.Type.ToString(), "type", property.Type.GetLocation());
        nodes.Add(typeNode);
        roles[typeNode.Id] = "property_type";

        var hasGet = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false;
        var hasSet = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false;

        this.AddHyperedge(HyperedgeType.PropertyDefinition, nodes, roles,
            ("has_get", hasGet), ("has_set", hasSet));
    }

    private void ExtractFieldDefinition(FieldDeclarationSyntax field)
    {
        foreach (var variable in field.Declaration.Variables)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var fieldNode = this.GetOrCreateNode(variable.Identifier.Text, "field", variable.GetLocation(),
                modifiers: GetModifiers(field.Modifiers));
            nodes.Add(fieldNode);
            roles[fieldNode.Id] = "field";

            var typeNode = this.GetOrCreateNode(field.Declaration.Type.ToString(), "type", field.Declaration.Type.GetLocation());
            nodes.Add(typeNode);
            roles[typeNode.Id] = "field_type";

            // Check for DI pattern (readonly interface field)
            var typeName = field.Declaration.Type.ToString();
            if (typeName.StartsWith("I") && typeName.Length > 1 && char.IsUpper(typeName[1]))
            {
                var isReadonly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
                var isPrivate = field.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
                if (isReadonly || isPrivate)
                {
                    this.AddHyperedge(HyperedgeType.DependencyInjection, nodes, roles,
                        ("injection_type", "field"));
                }
            }

            this.AddHyperedge(HyperedgeType.Assignment, nodes, roles,
                ("is_field", true));
        }
    }

    private void ExtractEventDefinition(EventDeclarationSyntax eventDecl)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var eventNode = this.GetOrCreateNode(eventDecl.Identifier.Text, "event", eventDecl.GetLocation());
        nodes.Add(eventNode);
        roles[eventNode.Id] = "event";

        var typeNode = this.GetOrCreateNode(eventDecl.Type.ToString(), "delegate_type", eventDecl.Type.GetLocation());
        nodes.Add(typeNode);
        roles[typeNode.Id] = "event_type";

        this.AddHyperedge(HyperedgeType.EventHandler, nodes, roles);
    }

    private void ExtractDependencyInjection(ParameterSyntax param)
    {
        if (param.Type == null) return;

        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var interfaceNode = this.GetOrCreateNode(param.Type.ToString(), "interface", param.Type.GetLocation());
        nodes.Add(interfaceNode);
        roles[interfaceNode.Id] = "injected_interface";

        var paramNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
        nodes.Add(paramNode);
        roles[paramNode.Id] = "injection_target";

        this.AddHyperedge(HyperedgeType.DependencyInjection, nodes, roles,
            ("injection_type", "constructor"));
    }

    private void ProcessStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            this.ProcessStatement(statement);
        }
    }

    private void ProcessStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDecl:
                this.ExtractLocalDeclaration(localDecl);
                break;
            case ExpressionStatementSyntax exprStmt:
                this.ProcessExpression(exprStmt.Expression);
                break;
            case IfStatementSyntax ifStmt:
                this.ExtractIfStatement(ifStmt);
                break;
            case ForEachStatementSyntax forEach:
                this.ExtractForEachStatement(forEach);
                break;
            case ForStatementSyntax forStmt:
                this.ExtractForStatement(forStmt);
                break;
            case WhileStatementSyntax whileStmt:
                this.ExtractWhileStatement(whileStmt);
                break;
            case TryStatementSyntax tryStmt:
                this.ExtractTryStatement(tryStmt);
                break;
            case ReturnStatementSyntax returnStmt:
                if (returnStmt.Expression != null)
                    this.ProcessExpression(returnStmt.Expression);
                break;
            case BlockSyntax block:
                this.ProcessStatements(block.Statements);
                break;
            case UsingStatementSyntax usingStmt:
                this.ExtractUsingStatement(usingStmt);
                break;
        }
    }

    private void ExtractLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
    {
        foreach (var variable in localDecl.Declaration.Variables)
        {
            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            var varNode = this.GetOrCreateNode(variable.Identifier.Text, "variable", variable.GetLocation());
            nodes.Add(varNode);
            roles[varNode.Id] = "target";

            if (variable.Initializer != null)
            {
                var valueRepr = GetExpressionRepr(variable.Initializer.Value);
                var valueNode = this.GetOrCreateNode(valueRepr, "expression", variable.Initializer.GetLocation());
                nodes.Add(valueNode);
                roles[valueNode.Id] = "value";

                this.ProcessExpression(variable.Initializer.Value);
            }

            var line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            this.TrackVariableDef(variable.Identifier.Text, $"{this.CurrentScope}:L{line}");

            this.AddHyperedge(HyperedgeType.Assignment, nodes, roles);
        }
    }

    private void ProcessExpression(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case InvocationExpressionSyntax invocation:
                this.ExtractMethodInvocation(invocation);
                break;
            case ObjectCreationExpressionSyntax objectCreation:
                this.ExtractObjectCreation(objectCreation);
                break;
            case AwaitExpressionSyntax awaitExpr:
                this.ExtractAwaitExpression(awaitExpr);
                break;
            case LambdaExpressionSyntax lambda:
                this.ExtractLambdaExpression(lambda);
                break;
            case QueryExpressionSyntax query:
                this.ExtractLinqQuery(query);
                break;
            case AssignmentExpressionSyntax assignment:
                this.ExtractAssignment(assignment);
                break;
            case IdentifierNameSyntax identifier:
                var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                this.TrackVariableUse(identifier.Identifier.Text, $"{this.CurrentScope}:L{line}");
                break;
            case MemberAccessExpressionSyntax memberAccess:
                this.ProcessExpression(memberAccess.Expression);
                break;
            case ConditionalExpressionSyntax conditional:
                this.ProcessExpression(conditional.Condition);
                this.ProcessExpression(conditional.WhenTrue);
                this.ProcessExpression(conditional.WhenFalse);
                break;
            case BinaryExpressionSyntax binary:
                this.ProcessExpression(binary.Left);
                this.ProcessExpression(binary.Right);
                break;
        }
    }

    private void ExtractMethodInvocation(InvocationExpressionSyntax invocation)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Method being called
        var methodName = GetExpressionRepr(invocation.Expression);
        var methodNode = this.GetOrCreateNode(methodName, "method_ref", invocation.Expression.GetLocation());
        nodes.Add(methodNode);
        roles[methodNode.Id] = "callee";

        // Arguments - ALL together in one hyperedge
        for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            var argRepr = GetExpressionRepr(arg.Expression);
            var argNode = this.GetOrCreateNode(argRepr, "expression", arg.GetLocation());
            nodes.Add(argNode);
            roles[argNode.Id] = arg.NameColon != null ? $"named_{arg.NameColon.Name}" : $"arg_{i}";

            this.ProcessExpression(arg.Expression);
        }

        if (nodes.Count > 1)
        {
            this.AddHyperedge(HyperedgeType.MethodInvocation, nodes, roles);
        }

        // Check for architectural patterns
        var simpleMethodName = GetSimpleMethodName(invocation.Expression);
        this.ExtractArchitecturalPatterns(invocation, simpleMethodName);
    }

    private void ExtractArchitecturalPatterns(InvocationExpressionSyntax invocation, string methodName)
    {
        // DI Service Registration
        if (DiRegistrationMethods.Contains(methodName))
        {
            this.ExtractServiceRegistration(invocation, methodName);
        }
        // Logging calls
        else if (LoggingMethods.Contains(methodName))
        {
            this.ExtractLoggingCall(invocation, methodName);
        }
        // HttpClient usage
        else if (IsHttpClientCall(invocation))
        {
            this.ExtractHttpClientUsage(invocation);
        }
        // File system access
        else if (FileSystemMethods.Contains(methodName))
        {
            this.ExtractFileSystemAccess(invocation, methodName);
        }
        // Configuration access
        else if (IsConfigurationAccess(invocation))
        {
            this.ExtractConfigurationAccess(invocation);
        }
        // Middleware registration
        else if (methodName.StartsWith("Use") && IsMiddlewareRegistration(invocation))
        {
            this.ExtractMiddlewareRegistration(invocation, methodName);
        }
    }

    private void ExtractServiceRegistration(InvocationExpressionSyntax invocation, string methodName)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // The DI method
        var methodNode = this.GetOrCreateNode(methodName, "di_method", invocation.GetLocation());
        nodes.Add(methodNode);
        roles[methodNode.Id] = "registration_method";

        // Determine lifetime
        var lifetime = methodName switch
        {
            var m when m.Contains("Singleton") => "Singleton",
            var m when m.Contains("Scoped") => "Scoped",
            var m when m.Contains("Transient") => "Transient",
            var m when m.Contains("HostedService") => "Singleton",
            _ => "Unknown"
        };

        string? serviceType = null;
        string? implementationType = null;

        // Check for generic type arguments: AddSingleton<IService, Implementation>()
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName)
        {
            var typeArgs = genericName.TypeArgumentList.Arguments;
            if (typeArgs.Count >= 1)
            {
                serviceType = typeArgs[0].ToString();
                var serviceNode = this.GetOrCreateNode(serviceType, "service_interface", invocation.GetLocation());
                nodes.Add(serviceNode);
                roles[serviceNode.Id] = "service_type";
            }
            if (typeArgs.Count >= 2)
            {
                implementationType = typeArgs[1].ToString();
                var implNode = this.GetOrCreateNode(implementationType, "service_implementation", invocation.GetLocation());
                nodes.Add(implNode);
                roles[implNode.Id] = "implementation_type";
            }
        }

        // Check arguments for typeof() expressions
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is TypeOfExpressionSyntax typeOf)
            {
                var typeName = typeOf.Type.ToString();
                if (serviceType == null)
                {
                    serviceType = typeName;
                    var serviceNode = this.GetOrCreateNode(typeName, "service_interface", arg.GetLocation());
                    nodes.Add(serviceNode);
                    roles[serviceNode.Id] = "service_type";
                }
                else if (implementationType == null)
                {
                    implementationType = typeName;
                    var implNode = this.GetOrCreateNode(typeName, "service_implementation", arg.GetLocation());
                    nodes.Add(implNode);
                    roles[implNode.Id] = "implementation_type";
                }
            }
            else if (arg.Expression is LambdaExpressionSyntax)
            {
                var factoryNode = this.GetOrCreateNode("factory", "factory_delegate", arg.GetLocation());
                nodes.Add(factoryNode);
                roles[factoryNode.Id] = "factory";
            }
        }

        this.AddHyperedge(HyperedgeType.ServiceRegistration, nodes, roles,
            ("lifetime", lifetime),
            ("service_type", serviceType),
            ("implementation_type", implementationType),
            ("method", methodName));

        if (serviceType != null)
        {
            this.registeredServices.Add(serviceType);
        }
    }

    private void ExtractLoggingCall(InvocationExpressionSyntax invocation, string methodName)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Log level from method name
        var logLevel = methodName switch
        {
            "LogTrace" => "Trace",
            "LogDebug" => "Debug",
            "LogInformation" => "Information",
            "LogWarning" => "Warning",
            "LogError" => "Error",
            "LogCritical" => "Critical",
            _ => "Unknown"
        };

        var logNode = this.GetOrCreateNode(methodName, "log_call", invocation.GetLocation());
        nodes.Add(logNode);
        roles[logNode.Id] = "log_method";

        string? messageTemplate = null;
        var structuredFields = new List<string>();

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                messageTemplate = literal.Token.ValueText;
                var templateNode = this.GetOrCreateNode(messageTemplate.Length > 50 ? messageTemplate[..50] + "..." : messageTemplate,
                    "log_template", arg.GetLocation());
                nodes.Add(templateNode);
                roles[templateNode.Id] = "message_template";

                // Extract structured logging placeholders {PropertyName}
                var matches = System.Text.RegularExpressions.Regex.Matches(messageTemplate, @"\{(\w+)\}");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    structuredFields.Add(match.Groups[1].Value);
                }
            }
            else if (arg.Expression is InterpolatedStringExpressionSyntax)
            {
                messageTemplate = GetExpressionRepr(arg.Expression);
            }
        }

        // Check if logging an exception
        var hasException = invocation.ArgumentList.Arguments.Any(a =>
            a.Expression.ToString().Contains("Exception") ||
            a.Expression.ToString().Contains("ex") ||
            a.Expression.ToString().Contains("error"));

        this.AddHyperedge(HyperedgeType.LoggingCall, nodes, roles,
            ("log_level", logLevel),
            ("message_template", messageTemplate),
            ("structured_fields", structuredFields.Count > 0 ? string.Join(", ", structuredFields) : null),
            ("has_exception", hasException),
            ("scope", this.CurrentScope));
    }

    private static bool IsHttpClientCall(InvocationExpressionSyntax invocation)
    {
        var expr = invocation.Expression.ToString();
        return expr.Contains("HttpClient") ||
               expr.Contains("GetAsync") ||
               expr.Contains("PostAsync") ||
               expr.Contains("PutAsync") ||
               expr.Contains("DeleteAsync") ||
               expr.Contains("SendAsync") ||
               expr.Contains("GetStringAsync") ||
               expr.Contains("GetStreamAsync");
    }

    private void ExtractHttpClientUsage(InvocationExpressionSyntax invocation)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var methodName = GetSimpleMethodName(invocation.Expression);
        var httpMethod = methodName switch
        {
            var m when m.Contains("Get") => "GET",
            var m when m.Contains("Post") => "POST",
            var m when m.Contains("Put") => "PUT",
            var m when m.Contains("Delete") => "DELETE",
            var m when m.Contains("Patch") => "PATCH",
            _ => "Unknown"
        };

        var callNode = this.GetOrCreateNode(methodName, "http_call", invocation.GetLocation());
        nodes.Add(callNode);
        roles[callNode.Id] = "http_method";

        string? url = null;
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var argStr = arg.Expression.ToString();
            if (argStr.Contains("http") || argStr.Contains("/api") || argStr.Contains("://"))
            {
                url = argStr;
                var urlNode = this.GetOrCreateNode(argStr.Length > 60 ? argStr[..60] + "..." : argStr,
                    "endpoint_url", arg.GetLocation());
                nodes.Add(urlNode);
                roles[urlNode.Id] = "target_url";
                break;
            }
        }

        this.AddHyperedge(HyperedgeType.HttpClientUsage, nodes, roles,
            ("http_method", httpMethod),
            ("url", url),
            ("scope", this.CurrentScope),
            ("is_async", methodName.EndsWith("Async")));
    }

    private void ExtractFileSystemAccess(InvocationExpressionSyntax invocation, string methodName)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var operation = methodName switch
        {
            var m when m.Contains("Read") => "Read",
            var m when m.Contains("Write") => "Write",
            var m when m.Contains("Create") => "Create",
            var m when m.Contains("Delete") => "Delete",
            var m when m.Contains("Copy") => "Copy",
            var m when m.Contains("Move") => "Move",
            var m when m.Contains("Exists") => "Check",
            var m when m.Contains("Get") || m.Contains("Enumerate") => "List",
            _ => "Access"
        };

        var fsNode = this.GetOrCreateNode(methodName, "file_operation", invocation.GetLocation());
        nodes.Add(fsNode);
        roles[fsNode.Id] = "operation";

        string? path = null;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0];
            path = GetExpressionRepr(firstArg.Expression);
            var pathNode = this.GetOrCreateNode(path, "file_path", firstArg.GetLocation());
            nodes.Add(pathNode);
            roles[pathNode.Id] = "target_path";
        }

        this.AddHyperedge(HyperedgeType.FileSystemAccess, nodes, roles,
            ("operation", operation),
            ("method", methodName),
            ("path", path),
            ("scope", this.CurrentScope));
    }

    private static bool IsConfigurationAccess(InvocationExpressionSyntax invocation)
    {
        var expr = invocation.Expression.ToString();
        return expr.Contains("Configuration") ||
               expr.Contains("GetSection") ||
               expr.Contains("GetValue") ||
               expr.Contains("IOptions") ||
               expr.Contains("config[");
    }

    private void ExtractConfigurationAccess(InvocationExpressionSyntax invocation)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var methodName = GetSimpleMethodName(invocation.Expression);
        var configNode = this.GetOrCreateNode(methodName, "config_access", invocation.GetLocation());
        nodes.Add(configNode);
        roles[configNode.Id] = "access_method";

        string? configKey = null;
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is LiteralExpressionSyntax literal)
            {
                configKey = literal.Token.ValueText;
                var keyNode = this.GetOrCreateNode(configKey, "config_key", arg.GetLocation());
                nodes.Add(keyNode);
                roles[keyNode.Id] = "configuration_key";
                break;
            }
        }

        this.AddHyperedge(HyperedgeType.ConfigurationAccess, nodes, roles,
            ("method", methodName),
            ("config_key", configKey),
            ("scope", this.CurrentScope));
    }

    private static bool IsMiddlewareRegistration(InvocationExpressionSyntax invocation)
    {
        var expr = invocation.Expression.ToString();
        return expr.Contains("app.Use") || expr.Contains("builder.Use") || expr.Contains("WebApplication");
    }

    private void ExtractMiddlewareRegistration(InvocationExpressionSyntax invocation, string methodName)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var middlewareName = methodName.StartsWith("Use") ? methodName[3..] : methodName;
        var mwNode = this.GetOrCreateNode(middlewareName, "middleware", invocation.GetLocation());
        nodes.Add(mwNode);
        roles[mwNode.Id] = "middleware";

        this.AddHyperedge(HyperedgeType.MiddlewarePipeline, nodes, roles,
            ("middleware_name", middlewareName),
            ("method", methodName));
    }

    private static string GetSimpleMethodName(ExpressionSyntax expression)
    {
        return expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => expression.ToString().Split('.').Last().Split('<').First()
        };
    }

    private void ExtractApiEndpoint(MethodDeclarationSyntax method)
    {
        string? httpMethod = null;
        string? routeTemplate = null;
        var produces = new List<string>();
        var consumes = new List<string>();

        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString().Replace("Attribute", "");

                switch (attrName)
                {
                    case "HttpGet":
                        httpMethod = "GET";
                        routeTemplate = GetAttributeArgument(attr);
                        break;
                    case "HttpPost":
                        httpMethod = "POST";
                        routeTemplate = GetAttributeArgument(attr);
                        break;
                    case "HttpPut":
                        httpMethod = "PUT";
                        routeTemplate = GetAttributeArgument(attr);
                        break;
                    case "HttpDelete":
                        httpMethod = "DELETE";
                        routeTemplate = GetAttributeArgument(attr);
                        break;
                    case "HttpPatch":
                        httpMethod = "PATCH";
                        routeTemplate = GetAttributeArgument(attr);
                        break;
                    case "Route":
                        routeTemplate ??= GetAttributeArgument(attr);
                        break;
                    case "Produces":
                        var producesType = GetAttributeArgument(attr);
                        if (producesType != null) produces.Add(producesType);
                        break;
                    case "Consumes":
                        var consumesType = GetAttributeArgument(attr);
                        if (consumesType != null) consumes.Add(consumesType);
                        break;
                }
            }
        }

        // If no HTTP method attribute, assume GET for methods named Get*
        if (httpMethod == null && method.Identifier.Text.StartsWith("Get"))
            httpMethod = "GET";
        else if (httpMethod == null && method.Identifier.Text.StartsWith("Post"))
            httpMethod = "POST";
        else if (httpMethod == null && method.Identifier.Text.StartsWith("Put"))
            httpMethod = "PUT";
        else if (httpMethod == null && method.Identifier.Text.StartsWith("Delete"))
            httpMethod = "DELETE";

        if (httpMethod == null)
            return; // Not an API endpoint

        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // Build full route
        var fullRoute = this.controllerRoute ?? "";
        if (routeTemplate != null)
        {
            fullRoute = fullRoute.TrimEnd('/') + "/" + routeTemplate.TrimStart('/');
        }
        fullRoute = fullRoute.Replace("[controller]", this.scopeStack.Peek().Replace("Controller", "").ToLowerInvariant());
        fullRoute = fullRoute.Replace("[action]", method.Identifier.Text.ToLowerInvariant());

        var endpointNode = this.GetOrCreateNode($"{httpMethod} {fullRoute}", "api_endpoint", method.GetLocation());
        nodes.Add(endpointNode);
        roles[endpointNode.Id] = "endpoint";

        var actionNode = this.GetOrCreateNode(method.Identifier.Text, "action_method", method.GetLocation());
        nodes.Add(actionNode);
        roles[actionNode.Id] = "action";

        // Return type
        var returnNode = this.GetOrCreateNode(method.ReturnType.ToString(), "return_type", method.ReturnType.GetLocation());
        nodes.Add(returnNode);
        roles[returnNode.Id] = "response_type";

        // Parameters with binding info
        foreach (var param in method.ParameterList.Parameters)
        {
            var paramNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
            nodes.Add(paramNode);

            var binding = "query"; // default
            foreach (var attrList in param.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrName = attr.Name.ToString();
                    if (attrName.Contains("FromBody")) binding = "body";
                    else if (attrName.Contains("FromRoute")) binding = "route";
                    else if (attrName.Contains("FromQuery")) binding = "query";
                    else if (attrName.Contains("FromHeader")) binding = "header";
                    else if (attrName.Contains("FromForm")) binding = "form";
                    else if (attrName.Contains("FromServices")) binding = "services";
                }
            }

            roles[paramNode.Id] = $"param_{binding}_{param.Identifier.Text}";
        }

        this.AddHyperedge(HyperedgeType.ApiEndpoint, nodes, roles,
            ("http_method", httpMethod),
            ("route", fullRoute),
            ("controller", this.scopeStack.Peek()),
            ("action", method.Identifier.Text),
            ("produces", produces.Count > 0 ? string.Join(", ", produces) : null),
            ("consumes", consumes.Count > 0 ? string.Join(", ", consumes) : null),
            ("is_async", method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))));
    }

    private static string? GetAttributeArgument(AttributeSyntax attr)
    {
        if (attr.ArgumentList?.Arguments.Count > 0)
        {
            var arg = attr.ArgumentList.Arguments[0].ToString();
            return arg.Trim('"', '\'', '@');
        }
        return null;
    }

    private void ExtractEntryPointIfMain(MethodDeclarationSyntax method)
    {
        // Check if this is a Main method
        if (method.Identifier.Text != "Main")
            return;

        var isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        if (!isStatic)
            return;

        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var mainNode = this.GetOrCreateNode("Main", "entry_point", method.GetLocation());
        nodes.Add(mainNode);
        roles[mainNode.Id] = "main_method";

        // Check return type
        var returnType = method.ReturnType.ToString();
        var isAsync = returnType.Contains("Task") || method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

        // Check for common startup patterns in the body
        var usesWebApplication = false;
        var usesHostBuilder = false;
        var usesStartup = false;

        if (method.Body != null)
        {
            var bodyText = method.Body.ToString();
            usesWebApplication = bodyText.Contains("WebApplication") || bodyText.Contains("CreateBuilder");
            usesHostBuilder = bodyText.Contains("HostBuilder") || bodyText.Contains("CreateDefaultBuilder");
            usesStartup = bodyText.Contains("UseStartup");
        }

        this.AddHyperedge(HyperedgeType.EntryPoint, nodes, roles,
            ("is_async", isAsync),
            ("return_type", returnType),
            ("uses_web_application", usesWebApplication),
            ("uses_host_builder", usesHostBuilder),
            ("uses_startup_class", usesStartup),
            ("file", this.filePath));
    }

    private void ExtractTopLevelStatements(CompilationUnitSyntax root)
    {
        // Check for top-level statements (C# 9+ minimal hosting)
        var hasTopLevelStatements = root.Members.Any(m => m is GlobalStatementSyntax);
        if (!hasTopLevelStatements)
            return;

        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var entryNode = this.GetOrCreateNode("TopLevelStatements", "entry_point", root.GetLocation());
        nodes.Add(entryNode);
        roles[entryNode.Id] = "program";

        // Analyze top-level statements for startup patterns
        var usesWebApplication = false;
        var usesMinimalApis = false;

        foreach (var member in root.Members.OfType<GlobalStatementSyntax>())
        {
            var stmtText = member.Statement.ToString();
            if (stmtText.Contains("WebApplication.CreateBuilder"))
                usesWebApplication = true;
            if (stmtText.Contains("app.Map") || stmtText.Contains("app.Get") || stmtText.Contains("app.Post"))
                usesMinimalApis = true;
        }

        this.AddHyperedge(HyperedgeType.EntryPoint, nodes, roles,
            ("is_top_level", true),
            ("uses_web_application", usesWebApplication),
            ("uses_minimal_apis", usesMinimalApis),
            ("file", this.filePath));
    }

    private void ExtractObjectCreation(ObjectCreationExpressionSyntax objectCreation)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var typeName = objectCreation.Type.ToString();
        var typeNode = this.GetOrCreateNode(typeName, "type", objectCreation.Type.GetLocation());
        nodes.Add(typeNode);
        roles[typeNode.Id] = "instantiated_type";

        if (objectCreation.ArgumentList != null)
        {
            for (int i = 0; i < objectCreation.ArgumentList.Arguments.Count; i++)
            {
                var arg = objectCreation.ArgumentList.Arguments[i];
                var argRepr = GetExpressionRepr(arg.Expression);
                var argNode = this.GetOrCreateNode(argRepr, "expression", arg.GetLocation());
                nodes.Add(argNode);
                roles[argNode.Id] = $"ctor_arg_{i}";

                this.ProcessExpression(arg.Expression);
            }
        }

        this.AddHyperedge(HyperedgeType.ConstructorInvocation, nodes, roles);

        // Check for database-related instantiations
        if (typeName.Contains("DbContext") || typeName.Contains("SqlConnection") ||
            typeName.Contains("SqlCommand") || typeName.Contains("DbConnection"))
        {
            this.ExtractDatabaseAccess(objectCreation, typeName);
        }
        // Check for HttpClient instantiation
        else if (typeName.Contains("HttpClient"))
        {
            this.ExtractHttpClientCreation(objectCreation);
        }
    }

    private void ExtractDatabaseAccess(ObjectCreationExpressionSyntax objectCreation, string typeName)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var dbNode = this.GetOrCreateNode(typeName, "database_type", objectCreation.GetLocation());
        nodes.Add(dbNode);
        roles[dbNode.Id] = "db_type";

        var dbTech = typeName switch
        {
            var t when t.Contains("DbContext") => "EntityFramework",
            var t when t.Contains("Sql") => "SqlServer",
            var t when t.Contains("Npgsql") => "PostgreSQL",
            var t when t.Contains("MySql") => "MySQL",
            var t when t.Contains("Sqlite") => "SQLite",
            _ => "Unknown"
        };

        // Check for connection string in arguments
        string? connectionString = null;
        if (objectCreation.ArgumentList?.Arguments.Count > 0)
        {
            var firstArg = objectCreation.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArg.Contains("connection") || firstArg.Contains("Connection"))
            {
                connectionString = firstArg;
                var connNode = this.GetOrCreateNode(firstArg.Length > 50 ? firstArg[..50] + "..." : firstArg,
                    "connection_string", objectCreation.ArgumentList.Arguments[0].GetLocation());
                nodes.Add(connNode);
                roles[connNode.Id] = "connection";
            }
        }

        this.AddHyperedge(HyperedgeType.DatabaseAccess, nodes, roles,
            ("db_type", typeName),
            ("technology", dbTech),
            ("connection_string_ref", connectionString),
            ("scope", this.CurrentScope));
    }

    private void ExtractHttpClientCreation(ObjectCreationExpressionSyntax objectCreation)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var clientNode = this.GetOrCreateNode("HttpClient", "http_client", objectCreation.GetLocation());
        nodes.Add(clientNode);
        roles[clientNode.Id] = "client";

        string? baseAddress = null;
        if (objectCreation.Initializer != null)
        {
            foreach (var expr in objectCreation.Initializer.Expressions)
            {
                if (expr is AssignmentExpressionSyntax assignment &&
                    assignment.Left.ToString().Contains("BaseAddress"))
                {
                    baseAddress = assignment.Right.ToString();
                    var baseNode = this.GetOrCreateNode(baseAddress, "base_address", assignment.GetLocation());
                    nodes.Add(baseNode);
                    roles[baseNode.Id] = "base_address";
                }
            }
        }

        this.AddHyperedge(HyperedgeType.HttpClientUsage, nodes, roles,
            ("creation", true),
            ("base_address", baseAddress),
            ("scope", this.CurrentScope));
    }

    private void ExtractAwaitExpression(AwaitExpressionSyntax awaitExpr)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var awaitNode = this.GetOrCreateNode("await", "keyword", awaitExpr.GetLocation());
        nodes.Add(awaitNode);
        roles[awaitNode.Id] = "await";

        var exprRepr = GetExpressionRepr(awaitExpr.Expression);
        var exprNode = this.GetOrCreateNode(exprRepr, "async_expression", awaitExpr.Expression.GetLocation());
        nodes.Add(exprNode);
        roles[exprNode.Id] = "awaited_task";

        this.AddHyperedge(HyperedgeType.AsyncAwait, nodes, roles);

        this.ProcessExpression(awaitExpr.Expression);
    }

    private void ExtractLambdaExpression(LambdaExpressionSyntax lambda)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var lambdaNode = this.GetOrCreateNode("lambda", "lambda", lambda.GetLocation());
        nodes.Add(lambdaNode);
        roles[lambdaNode.Id] = "lambda";

        // Parameters
        switch (lambda)
        {
            case SimpleLambdaExpressionSyntax simple:
                var paramNode = this.GetOrCreateNode(simple.Parameter.Identifier.Text, "parameter", simple.Parameter.GetLocation());
                nodes.Add(paramNode);
                roles[paramNode.Id] = "param_0";
                break;
            case ParenthesizedLambdaExpressionSyntax parens:
                for (int i = 0; i < parens.ParameterList.Parameters.Count; i++)
                {
                    var param = parens.ParameterList.Parameters[i];
                    var pNode = this.GetOrCreateNode(param.Identifier.Text, "parameter", param.GetLocation());
                    nodes.Add(pNode);
                    roles[pNode.Id] = $"param_{i}";
                }
                break;
        }

        this.AddHyperedge(HyperedgeType.LambdaExpression, nodes, roles);
    }

    private void ExtractLinqQuery(QueryExpressionSyntax query)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        // From clause
        var fromNode = this.GetOrCreateNode(query.FromClause.Identifier.Text, "range_variable", query.FromClause.GetLocation());
        nodes.Add(fromNode);
        roles[fromNode.Id] = "from";

        var sourceNode = this.GetOrCreateNode(GetExpressionRepr(query.FromClause.Expression), "source", query.FromClause.Expression.GetLocation());
        nodes.Add(sourceNode);
        roles[sourceNode.Id] = "source";

        // Query body clauses
        int clauseIndex = 0;
        foreach (var clause in query.Body.Clauses)
        {
            var clauseNode = this.GetOrCreateNode(clause.ToString().Split(' ')[0], "query_clause", clause.GetLocation());
            nodes.Add(clauseNode);
            roles[clauseNode.Id] = $"clause_{clauseIndex++}";
        }

        this.AddHyperedge(HyperedgeType.LinqQuery, nodes, roles);
    }

    private void ExtractAssignment(AssignmentExpressionSyntax assignment)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var targetRepr = GetExpressionRepr(assignment.Left);
        var targetNode = this.GetOrCreateNode(targetRepr, "variable", assignment.Left.GetLocation());
        nodes.Add(targetNode);
        roles[targetNode.Id] = "target";

        var valueRepr = GetExpressionRepr(assignment.Right);
        var valueNode = this.GetOrCreateNode(valueRepr, "expression", assignment.Right.GetLocation());
        nodes.Add(valueNode);
        roles[valueNode.Id] = "value";

        this.AddHyperedge(HyperedgeType.Assignment, nodes, roles);

        this.ProcessExpression(assignment.Right);
    }

    private void ExtractIfStatement(IfStatementSyntax ifStmt)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var conditionRepr = GetExpressionRepr(ifStmt.Condition);
        var conditionNode = this.GetOrCreateNode(conditionRepr, "condition", ifStmt.Condition.GetLocation());
        nodes.Add(conditionNode);
        roles[conditionNode.Id] = "condition";

        this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
            ("control_type", "if"));

        this.ProcessStatement(ifStmt.Statement);
        if (ifStmt.Else != null)
        {
            this.ProcessStatement(ifStmt.Else.Statement);
        }
    }

    private void ExtractForEachStatement(ForEachStatementSyntax forEach)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var iteratorNode = this.GetOrCreateNode(forEach.Identifier.Text, "variable", forEach.GetLocation());
        nodes.Add(iteratorNode);
        roles[iteratorNode.Id] = "iterator";

        var collectionRepr = GetExpressionRepr(forEach.Expression);
        var collectionNode = this.GetOrCreateNode(collectionRepr, "collection", forEach.Expression.GetLocation());
        nodes.Add(collectionNode);
        roles[collectionNode.Id] = "iterable";

        var line = forEach.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        this.TrackVariableDef(forEach.Identifier.Text, $"{this.CurrentScope}:L{line}");

        this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
            ("control_type", "foreach"));

        this.ProcessStatement(forEach.Statement);
    }

    private void ExtractForStatement(ForStatementSyntax forStmt)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        if (forStmt.Condition != null)
        {
            var conditionRepr = GetExpressionRepr(forStmt.Condition);
            var conditionNode = this.GetOrCreateNode(conditionRepr, "condition", forStmt.Condition.GetLocation());
            nodes.Add(conditionNode);
            roles[conditionNode.Id] = "condition";
        }

        this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
            ("control_type", "for"));

        this.ProcessStatement(forStmt.Statement);
    }

    private void ExtractWhileStatement(WhileStatementSyntax whileStmt)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var conditionRepr = GetExpressionRepr(whileStmt.Condition);
        var conditionNode = this.GetOrCreateNode(conditionRepr, "condition", whileStmt.Condition.GetLocation());
        nodes.Add(conditionNode);
        roles[conditionNode.Id] = "condition";

        this.AddHyperedge(HyperedgeType.ControlFlow, nodes, roles,
            ("control_type", "while"));

        this.ProcessStatement(whileStmt.Statement);
    }

    private void ExtractTryStatement(TryStatementSyntax tryStmt)
    {
        var nodes = new List<HypergraphNode>();
        var roles = new Dictionary<string, string>();

        var line = tryStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var tryNode = this.GetOrCreateNode($"try_L{line}", "try_block", tryStmt.GetLocation());
        nodes.Add(tryNode);
        roles[tryNode.Id] = "try_block";

        for (int i = 0; i < tryStmt.Catches.Count; i++)
        {
            var catchClause = tryStmt.Catches[i];
            var exceptionType = catchClause.Declaration?.Type.ToString() ?? "Exception";
            var catchNode = this.GetOrCreateNode(exceptionType, "exception_handler", catchClause.GetLocation());
            nodes.Add(catchNode);
            roles[catchNode.Id] = $"handler_{i}";
        }

        if (tryStmt.Finally != null)
        {
            var finallyNode = this.GetOrCreateNode($"finally_L{line}", "finally_block", tryStmt.Finally.GetLocation());
            nodes.Add(finallyNode);
            roles[finallyNode.Id] = "finally";
        }

        this.AddHyperedge(HyperedgeType.ExceptionHandling, nodes, roles);

        this.ProcessStatements(tryStmt.Block.Statements);
        foreach (var catchClause in tryStmt.Catches)
        {
            this.ProcessStatements(catchClause.Block.Statements);
        }
        if (tryStmt.Finally != null)
        {
            this.ProcessStatements(tryStmt.Finally.Block.Statements);
        }
    }

    private void ExtractUsingStatement(UsingStatementSyntax usingStmt)
    {
        if (usingStmt.Declaration != null)
        {
            foreach (var variable in usingStmt.Declaration.Variables)
            {
                var line = variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                this.TrackVariableDef(variable.Identifier.Text, $"{this.CurrentScope}:L{line}");
            }
        }

        this.ProcessStatement(usingStmt.Statement);
    }

    private void ExtractDataFlowHyperedges()
    {
        foreach (var (varName, defLocations) in this.variableDefs)
        {
            if (!this.variableUses.TryGetValue(varName, out var useLocations) || useLocations.Count == 0)
                continue;

            var nodes = new List<HypergraphNode>();
            var roles = new Dictionary<string, string>();

            for (int i = 0; i < defLocations.Count; i++)
            {
                var defNode = this.GetOrCreateNode($"{varName}@{defLocations[i]}", "definition", null);
                nodes.Add(defNode);
                roles[defNode.Id] = $"def_{i}";
            }

            for (int i = 0; i < useLocations.Count; i++)
            {
                var useNode = this.GetOrCreateNode($"{varName}@{useLocations[i]}", "use", null);
                nodes.Add(useNode);
                roles[useNode.Id] = $"use_{i}";
            }

            if (nodes.Count > 1)
            {
                this.AddHyperedge(HyperedgeType.DataFlow, nodes, roles,
                    ("variable", varName),
                    ("num_defs", defLocations.Count),
                    ("num_uses", useLocations.Count));
            }
        }
    }

    private HypergraphNode GetOrCreateNode(string name, string nodeType, Location? location, List<string>? modifiers = null)
    {
        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
        var nodeId = this.GenerateNodeId(name, nodeType, line);

        if (!this.nodes.TryGetValue(nodeId, out var node))
        {
            node = new HypergraphNode
            {
                Id = nodeId,
                Name = name,
                NodeType = nodeType,
                Language = "csharp",
                FilePath = this.filePath,
                Scope = this.CurrentScope,
                LineStart = line,
                LineEnd = location?.GetLineSpan().EndLinePosition.Line + 1 ?? line,
                Modifiers = modifiers ?? []
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
            NodeIds = nodes.Select(n => n.Id).ToList(),
            Roles = roles,
            Languages = ["csharp"],
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

    private static List<string> GetModifiers(SyntaxTokenList modifiers)
    {
        return modifiers.Select(m => m.Text).ToList();
    }

    private static string GetExpressionRepr(ExpressionSyntax expr)
    {
        var text = expr.ToString();
        return text.Length > 50 ? text[..50] + "..." : text;
    }
}