// -----------------------------------------------------------------------
// <copyright file="ErrorHandlingAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes error handling patterns including exceptions, try-catch, and resilience policies.
/// </summary>
public class ErrorHandlingAnalyzer
{
    private static readonly HashSet<string> PollyRetryMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Retry", "RetryAsync", "WaitAndRetry", "WaitAndRetryAsync",
        "RetryForever", "RetryForeverAsync",
    };

    private static readonly HashSet<string> PollyCircuitBreakerMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "CircuitBreaker", "CircuitBreakerAsync",
        "AdvancedCircuitBreaker", "AdvancedCircuitBreakerAsync",
    };

    private static readonly HashSet<string> PollyFallbackMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fallback", "FallbackAsync",
    };

    private static readonly HashSet<string> PollyTimeoutMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timeout", "TimeoutAsync",
    };

    private static readonly HashSet<string> PollyBulkheadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bulkhead", "BulkheadAsync",
    };

    /// <summary>
    /// Analyzes exception handling patterns in the given syntax tree.
    /// </summary>
    public List<ExceptionInfo> AnalyzeExceptions(SyntaxTree tree, string filePath)
    {
        var results = new List<ExceptionInfo>();
        var root = tree.GetRoot();

        // Analyze throw statements
        foreach (var throwStmt in root.DescendantNodes().OfType<ThrowStatementSyntax>())
        {
            var info = this.AnalyzeThrowStatement(throwStmt, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // Analyze throw expressions (C# 7+)
        foreach (var throwExpr in root.DescendantNodes().OfType<ThrowExpressionSyntax>())
        {
            var info = this.AnalyzeThrowExpression(throwExpr, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // Analyze catch clauses
        foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var info = this.AnalyzeCatchClause(catchClause, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes try-catch-finally blocks in the given syntax tree.
    /// </summary>
    public List<TryCatchInfo> AnalyzeTryCatchBlocks(SyntaxTree tree, string filePath)
    {
        var results = new List<TryCatchInfo>();
        var root = tree.GetRoot();

        foreach (var tryStmt in root.DescendantNodes().OfType<TryStatementSyntax>())
        {
            var info = this.AnalyzeTryStatement(tryStmt, filePath);
            results.Add(info);
        }

        return results;
    }

    /// <summary>
    /// Analyzes resilience policies (Polly, .NET 8 Resilience) in the given syntax tree.
    /// </summary>
    public List<ResiliencePolicyInfo> AnalyzeResiliencePolicies(SyntaxTree tree, string filePath)
    {
        var results = new List<ResiliencePolicyInfo>();
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var info = this.AnalyzePollyInvocation(invocation, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        // .NET 8 Resilience pipeline
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var info = this.AnalyzeResiliencePipeline(creation, filePath);
            if (info != null)
            {
                results.Add(info);
            }
        }

        return results;
    }

    private ExceptionInfo? AnalyzeThrowStatement(ThrowStatementSyntax throwStmt, string filePath)
    {
        var containingClass = throwStmt.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = throwStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var containingCatch = throwStmt.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();

        string? exceptionType = null;
        string? message = null;
        bool isRethrow = false;

        if (throwStmt.Expression == null)
        {
            // Bare rethrow: throw;
            isRethrow = true;
            if (containingCatch?.Declaration != null)
            {
                exceptionType = containingCatch.Declaration.Type.ToString();
            }
        }
        else if (throwStmt.Expression is ObjectCreationExpressionSyntax creation)
        {
            exceptionType = creation.Type.ToString();
            if (creation.ArgumentList?.Arguments.Count > 0)
            {
                var firstArg = creation.ArgumentList.Arguments[0].Expression;
                if (firstArg is LiteralExpressionSyntax literal)
                {
                    message = literal.Token.ValueText;
                }
                else if (firstArg is InterpolatedStringExpressionSyntax)
                {
                    message = firstArg.ToString();
                }
            }
        }
        else if (throwStmt.Expression is IdentifierNameSyntax identifier)
        {
            // Rethrowing a caught exception: throw ex;
            isRethrow = true;
            exceptionType = identifier.Identifier.Text;
        }

        return new ExceptionInfo
        {
            ExceptionType = exceptionType ?? "Exception",
            Operation = isRethrow ? ExceptionOperation.Rethrow : ExceptionOperation.Throw,
            Message = message,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = throwStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private ExceptionInfo? AnalyzeThrowExpression(ThrowExpressionSyntax throwExpr, string filePath)
    {
        var containingClass = throwExpr.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = throwExpr.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? exceptionType = null;
        string? message = null;

        if (throwExpr.Expression is ObjectCreationExpressionSyntax creation)
        {
            exceptionType = creation.Type.ToString();
            if (creation.ArgumentList?.Arguments.Count > 0)
            {
                var firstArg = creation.ArgumentList.Arguments[0].Expression;
                if (firstArg is LiteralExpressionSyntax literal)
                {
                    message = literal.Token.ValueText;
                }
            }
        }

        return new ExceptionInfo
        {
            ExceptionType = exceptionType ?? "Exception",
            Operation = ExceptionOperation.Throw,
            Message = message,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = throwExpr.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private ExceptionInfo? AnalyzeCatchClause(CatchClauseSyntax catchClause, string filePath)
    {
        var containingClass = catchClause.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = catchClause.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        string? exceptionType = null;
        string? variableName = null;
        string? whenFilter = null;

        if (catchClause.Declaration != null)
        {
            exceptionType = catchClause.Declaration.Type.ToString();
            variableName = catchClause.Declaration.Identifier.Text;
        }

        if (catchClause.Filter != null)
        {
            whenFilter = catchClause.Filter.FilterExpression.ToString();
        }

        // Check if there's a rethrow inside the catch block
        var hasRethrow = catchClause.Block?.DescendantNodes()
            .OfType<ThrowStatementSyntax>()
            .Any() ?? false;

        return new ExceptionInfo
        {
            ExceptionType = exceptionType ?? "Exception",
            Operation = ExceptionOperation.Catch,
            VariableName = variableName,
            WhenFilter = whenFilter,
            HasRethrow = hasRethrow,
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = catchClause.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private TryCatchInfo AnalyzeTryStatement(TryStatementSyntax tryStmt, string filePath)
    {
        var containingClass = tryStmt.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = tryStmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        var catches = tryStmt.Catches.Select(c => new CatchInfo
        {
            ExceptionType = c.Declaration?.Type.ToString() ?? "Exception",
            VariableName = c.Declaration?.Identifier.Text,
            HasFilter = c.Filter != null,
            FilterExpression = c.Filter?.FilterExpression.ToString(),
        }).ToList();

        return new TryCatchInfo
        {
            CatchCount = tryStmt.Catches.Count,
            Catches = catches,
            HasFinally = tryStmt.Finally != null,
            IsNested = tryStmt.Ancestors().OfType<TryStatementSyntax>().Any(),
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = tryStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.95f,
        };
    }

    private ResiliencePolicyInfo? AnalyzePollyInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var methodName = memberAccess.Name.Identifier.Text;
        var receiver = memberAccess.Expression.ToString();

        ResiliencePolicyType? policyType = null;

        if (PollyRetryMethods.Contains(methodName))
        {
            policyType = ResiliencePolicyType.Retry;
        }
        else if (PollyCircuitBreakerMethods.Contains(methodName))
        {
            policyType = ResiliencePolicyType.CircuitBreaker;
        }
        else if (PollyFallbackMethods.Contains(methodName))
        {
            policyType = ResiliencePolicyType.Fallback;
        }
        else if (PollyTimeoutMethods.Contains(methodName))
        {
            policyType = ResiliencePolicyType.Timeout;
        }
        else if (PollyBulkheadMethods.Contains(methodName))
        {
            policyType = ResiliencePolicyType.Bulkhead;
        }
        else if (methodName == "Wrap" || methodName == "WrapAsync")
        {
            policyType = ResiliencePolicyType.PolicyWrap;
        }

        if (policyType == null)
        {
            return null;
        }

        var containingClass = invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        // Extract configuration
        int? retryCount = null;
        string? timeout = null;
        if (invocation.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal && int.TryParse(literal.Token.ValueText, out var count))
            {
                retryCount = count;
            }
        }

        return new ResiliencePolicyInfo
        {
            PolicyType = policyType.Value,
            MethodName = methodName,
            RetryCount = retryCount,
            Timeout = timeout,
            IsAsync = methodName.EndsWith("Async"),
            Technology = "Polly",
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.90f,
        };
    }

    private ResiliencePolicyInfo? AnalyzeResiliencePipeline(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();
        if (!typeName.Contains("ResiliencePipeline") && !typeName.Contains("ResilienceStrategy"))
        {
            return null;
        }

        var containingClass = creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingMethod = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return new ResiliencePolicyInfo
        {
            PolicyType = ResiliencePolicyType.Pipeline,
            MethodName = "new " + typeName,
            IsAsync = typeName.Contains("Async"),
            Technology = ".NET8Resilience",
            ClassName = containingClass?.Identifier.Text,
            MethodContext = containingMethod?.Identifier.Text,
            FilePath = filePath,
            LineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Confidence = 0.85f,
        };
    }
}

#region Model Classes

/// <summary>
/// Exception information.
/// </summary>
public class ExceptionInfo
{
    public required string ExceptionType { get; set; }

    public ExceptionOperation Operation { get; set; }

    public string? Message { get; set; }

    public string? VariableName { get; set; }

    public string? WhenFilter { get; set; }

    public bool HasRethrow { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Try-catch block information.
/// </summary>
public class TryCatchInfo
{
    public int CatchCount { get; set; }

    public List<CatchInfo> Catches { get; set; } = new();

    public bool HasFinally { get; set; }

    public bool IsNested { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

/// <summary>
/// Catch clause information.
/// </summary>
public class CatchInfo
{
    public required string ExceptionType { get; set; }

    public string? VariableName { get; set; }

    public bool HasFilter { get; set; }

    public string? FilterExpression { get; set; }
}

/// <summary>
/// Resilience policy information.
/// </summary>
public class ResiliencePolicyInfo
{
    public ResiliencePolicyType PolicyType { get; set; }

    public required string MethodName { get; set; }

    public int? RetryCount { get; set; }

    public string? Timeout { get; set; }

    public bool IsAsync { get; set; }

    public required string Technology { get; set; }

    public string? ClassName { get; set; }

    public string? MethodContext { get; set; }

    public required string FilePath { get; set; }

    public int LineNumber { get; set; }

    public float Confidence { get; set; }
}

#endregion

#region Enums

/// <summary>
/// Exception operation type.
/// </summary>
public enum ExceptionOperation
{
    Throw,
    Catch,
    Rethrow,
}

/// <summary>
/// Resilience policy type.
/// </summary>
public enum ResiliencePolicyType
{
    Retry,
    CircuitBreaker,
    Fallback,
    Timeout,
    Bulkhead,
    PolicyWrap,
    Pipeline,
}

#endregion
