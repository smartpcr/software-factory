// -----------------------------------------------------------------------
// <copyright file="EngineReflection.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Reflection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

/// <summary>
/// Shared reflection helpers for engine parity tests.
/// Uses an explicit allow-list so internal enums like CheckpointFrequency
/// do not silently enter the fixture.
/// </summary>
public static class EngineReflection
{
    private static readonly HashSet<string> AllowedEnumNames = new(StringComparer.Ordinal)
    {
        "RuntimeType",
        "NodeExecutionStatus",
        "WorkflowExecutionStatus",
        "MessageType",
        "NodePriority",
        "JoinType",
        "ExecutionMode",
    };

    /// <summary>
    /// Returns the 7 public enums that are part of the UI parity contract.
    /// </summary>
    public static IEnumerable<Type> AllPublicEnums()
    {
        var enumNamespace = "ExecutionEngine.Enums";
        return typeof(ExecutionEngine.Enums.RuntimeType).Assembly
            .GetExportedTypes()
            .Where(t => t.IsEnum && t.Namespace == enumNamespace && AllowedEnumNames.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the member names of an enum in source-declaration order (Enum.GetValues order).
    /// </summary>
    public static IReadOnlyList<string> MembersOf(Type enumType)
    {
        return Enum.GetValues(enumType)
            .Cast<object>()
            .Select(v => Enum.GetName(enumType, v)!)
            .ToList();
    }

    /// <summary>
    /// Returns the ordinal values of an enum in source-declaration order.
    /// </summary>
    public static IReadOnlyList<int> OrdinalsOf(Type enumType)
    {
        return Enum.GetValues(enumType)
            .Cast<object>()
            .Select(v => (int)v)
            .ToList();
    }
}
