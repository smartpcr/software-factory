// -----------------------------------------------------------------------
// <copyright file="ScheduledJobAnalyzer.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Analyzers;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Analyzes scheduled job patterns including Timer, PeriodicTimer, Hangfire, and Quartz.NET.
/// </summary>
public sealed class ScheduledJobAnalyzer
{
    private static readonly HashSet<string> TimerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timer",
        "PeriodicTimer",
        "System.Threading.Timer",
        "System.Timers.Timer"
    };

    private static readonly HashSet<string> HangfireMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enqueue",
        "Schedule",
        "AddOrUpdate",
        "ContinueWith",
        "ContinueJobWith"
    };

    private static readonly HashSet<string> HangfireReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "BackgroundJob",
        "RecurringJob"
    };

    // Cron expression pattern (5 or 6 fields)
    private static readonly Regex CronPattern = new(
        @"^[\*\d,\-\/\?LW#]+(\s+[\*\d,\-\/\?LW#]+){4,5}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a syntax tree for scheduled job patterns.
    /// </summary>
    /// <param name="syntaxTree">The syntax tree to analyze.</param>
    /// <param name="filePath">The source file path.</param>
    /// <returns>List of detected scheduled jobs.</returns>
    public IReadOnlyList<ScheduledJobInfo> Analyze(SyntaxTree syntaxTree, string filePath)
    {
        var root = syntaxTree.GetRoot();
        var jobs = new List<ScheduledJobInfo>();

        // Analyze object creations for Timer types
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var timerJob = this.AnalyzeTimerCreation(creation, filePath);
            if (timerJob != null)
            {
                jobs.Add(timerJob);
            }
        }

        // Analyze invocations for Hangfire
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var hangfireJob = this.AnalyzeHangfireInvocation(invocation, filePath);
            if (hangfireJob != null)
            {
                jobs.Add(hangfireJob);
            }
        }

        // Analyze class declarations for IJob (Quartz.NET)
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var quartzJob = this.AnalyzeQuartzJob(classDecl, filePath);
            if (quartzJob != null)
            {
                jobs.Add(quartzJob);
            }
        }

        return jobs;
    }

    private ScheduledJobInfo? AnalyzeTimerCreation(ObjectCreationExpressionSyntax creation, string filePath)
    {
        var typeName = creation.Type.ToString();

        // Check if it's a timer type
        if (!TimerTypes.Any(t => typeName.Contains(t)))
        {
            return null;
        }

        var lineNumber = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var containingClass = GetContainingTypeName(creation);

        // Extract interval from constructor arguments
        var interval = ExtractTimerInterval(creation);

        // Determine timer type
        var timerType = typeName.Contains("PeriodicTimer")
            ? ScheduledJobType.PeriodicTimer
            : typeName.Contains("System.Timers")
                ? ScheduledJobType.SystemTimer
                : ScheduledJobType.ThreadingTimer;

        // Try to extract callback method
        var callbackMethod = ExtractTimerCallback(creation);

        return new ScheduledJobInfo
        {
            JobType = timerType,
            ClassName = containingClass,
            CallbackMethod = callbackMethod,
            Interval = interval,
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private ScheduledJobInfo? AnalyzeHangfireInvocation(InvocationExpressionSyntax invocation, string filePath)
    {
        var methodName = GetMethodName(invocation);
        if (string.IsNullOrEmpty(methodName) || !HangfireMethods.Contains(methodName))
        {
            return null;
        }

        var receiver = GetReceiverName(invocation);
        if (string.IsNullOrEmpty(receiver) || !HangfireReceivers.Any(r => receiver.Contains(r)))
        {
            return null;
        }

        var lineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var containingClass = GetContainingTypeName(invocation);

        // Determine job type based on receiver and method
        var jobType = receiver.Contains("RecurringJob")
            ? ScheduledJobType.HangfireRecurring
            : methodName == "Schedule"
                ? ScheduledJobType.HangfireScheduled
                : ScheduledJobType.HangfireEnqueued;

        // Extract cron expression for recurring jobs
        string? cronExpression = null;
        string? jobId = null;

        if (invocation.ArgumentList != null)
        {
            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                var argValue = arg.Expression.ToString().Trim('"');

                // Check for cron expression
                if (IsCronExpression(argValue))
                {
                    cronExpression = argValue;
                }

                // First string argument is often the job ID
                if (arg.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                    jobId == null)
                {
                    jobId = literal.Token.ValueText;
                }
            }
        }

        // Extract the job method reference
        var jobMethod = ExtractHangfireJobMethod(invocation);

        return new ScheduledJobInfo
        {
            JobType = jobType,
            JobId = jobId,
            ClassName = containingClass,
            JobMethod = jobMethod,
            CronExpression = cronExpression,
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private ScheduledJobInfo? AnalyzeQuartzJob(ClassDeclarationSyntax classDecl, string filePath)
    {
        if (classDecl.BaseList == null)
        {
            return null;
        }

        var implementsIJob = classDecl.BaseList.Types
            .Any(t => t.Type.ToString() == "IJob");

        if (!implementsIJob)
        {
            return null;
        }

        var className = classDecl.Identifier.Text;
        var namespaceName = GetNamespace(classDecl);
        var fullName = string.IsNullOrEmpty(namespaceName) ? className : $"{namespaceName}.{className}";
        var lineNumber = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Check for Execute method
        var hasExecute = classDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Execute");

        // Look for DisallowConcurrentExecution attribute
        var disallowConcurrent = classDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString().Contains("DisallowConcurrentExecution"));

        return new ScheduledJobInfo
        {
            JobType = ScheduledJobType.QuartzJob,
            ClassName = fullName,
            HasExecuteMethod = hasExecute,
            DisallowConcurrentExecution = disallowConcurrent,
            FilePath = filePath,
            LineNumber = lineNumber
        };
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static string? GetReceiverName(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression.ToString();
        }

        return null;
    }

    private static string? ExtractTimerInterval(ObjectCreationExpressionSyntax creation)
    {
        if (creation.ArgumentList == null || !creation.ArgumentList.Arguments.Any())
        {
            return null;
        }

        // For System.Threading.Timer, interval is typically the 3rd or 4th argument
        // For PeriodicTimer, it's the first argument (TimeSpan)
        foreach (var arg in creation.ArgumentList.Arguments)
        {
            var argText = arg.Expression.ToString();

            // Check for TimeSpan.FromXxx patterns
            if (argText.Contains("TimeSpan.From"))
            {
                return argText;
            }

            // Check for numeric literals (milliseconds)
            if (arg.Expression is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                return $"{literal.Token.Value}ms";
            }
        }

        return creation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
    }

    private static string? ExtractTimerCallback(ObjectCreationExpressionSyntax creation)
    {
        if (creation.ArgumentList == null || !creation.ArgumentList.Arguments.Any())
        {
            return null;
        }

        var firstArg = creation.ArgumentList.Arguments.First().Expression;

        return firstArg switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            SimpleLambdaExpressionSyntax => "<lambda>",
            ParenthesizedLambdaExpressionSyntax => "<lambda>",
            _ => null
        };
    }

    private static string? ExtractHangfireJobMethod(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList == null || !invocation.ArgumentList.Arguments.Any())
        {
            return null;
        }

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            // Look for lambda expressions or method group
            switch (arg.Expression)
            {
                case SimpleLambdaExpressionSyntax lambda:
                    if (lambda.Body is InvocationExpressionSyntax lambdaInvocation)
                    {
                        return GetMethodName(lambdaInvocation);
                    }

                    break;

                case ParenthesizedLambdaExpressionSyntax parenLambda:
                    if (parenLambda.Body is InvocationExpressionSyntax parenInvocation)
                    {
                        return GetMethodName(parenInvocation);
                    }

                    break;

                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Name.Identifier.Text;
            }
        }

        return null;
    }

    private static bool IsCronExpression(string value)
    {
        // Quick check for cron-like patterns
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Check for Hangfire cron constants
        if (value.StartsWith("Cron."))
        {
            return true;
        }

        // Check for actual cron expression
        return CronPattern.IsMatch(value.Trim());
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
            {
                var namespaceName = GetNamespace(typeDecl);
                var typeName = typeDecl.Identifier.Text;
                return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
            }

            current = current.Parent;
        }

        return "Unknown";
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
}

/// <summary>
/// Information about a detected scheduled job.
/// </summary>
public record ScheduledJobInfo
{
    /// <summary>Gets the type of scheduled job.</summary>
    public required ScheduledJobType JobType { get; init; }

    /// <summary>Gets the job ID (for Hangfire recurring jobs).</summary>
    public string? JobId { get; init; }

    /// <summary>Gets the containing class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets the callback method for timers.</summary>
    public string? CallbackMethod { get; init; }

    /// <summary>Gets the job method for Hangfire jobs.</summary>
    public string? JobMethod { get; init; }

    /// <summary>Gets the interval for timer-based jobs.</summary>
    public string? Interval { get; init; }

    /// <summary>Gets the cron expression for recurring jobs.</summary>
    public string? CronExpression { get; init; }

    /// <summary>Gets whether the Quartz job has an Execute method.</summary>
    public bool HasExecuteMethod { get; init; }

    /// <summary>Gets whether concurrent execution is disallowed.</summary>
    public bool DisallowConcurrentExecution { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Types of scheduled jobs.
/// </summary>
public enum ScheduledJobType
{
    /// <summary>System.Threading.Timer.</summary>
    ThreadingTimer,

    /// <summary>System.Timers.Timer.</summary>
    SystemTimer,

    /// <summary>PeriodicTimer (.NET 6+).</summary>
    PeriodicTimer,

    /// <summary>Hangfire enqueued (fire-and-forget) job.</summary>
    HangfireEnqueued,

    /// <summary>Hangfire scheduled (delayed) job.</summary>
    HangfireScheduled,

    /// <summary>Hangfire recurring job.</summary>
    HangfireRecurring,

    /// <summary>Quartz.NET IJob implementation.</summary>
    QuartzJob
}
