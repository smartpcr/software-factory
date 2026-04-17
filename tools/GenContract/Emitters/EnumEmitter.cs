// -----------------------------------------------------------------------
// <copyright file="EnumEmitter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Emits TypeScript string-literal union types for C# enums.
/// </summary>
public static class EnumEmitter
{
    /// <summary>
    /// The names of enums in the parity contract (source-declaration order).
    /// </summary>
    public static readonly string[] AllowedEnumNames =
    [
        "ExecutionMode",
        "JoinType",
        "MessageType",
        "NodeExecutionStatus",
        "NodePriority",
        "RetryStrategy",
        "RuntimeType",
        "WorkflowExecutionStatus",
    ];

    /// <summary>
    /// Emits all enum unions to the writer.
    /// </summary>
    public static void Emit(TsWriter w, IReadOnlyList<Type> enumTypes)
    {
        foreach (var et in enumTypes)
        {
            TypeMapping.RegisterEnumName(et.Name);
            var members = Enum.GetValues(et)
                .Cast<object>()
                .Select(v => Enum.GetName(et, v)!)
                .ToList();

            w.Line($"export type {et.Name} =");
            w.Indent();
            for (var i = 0; i < members.Count; i++)
            {
                var sep = i < members.Count - 1 ? " |" : ";";
                w.Line($"| '{members[i]}'{(i < members.Count - 1 ? "" : ";")}");
            }

            w.Dedent();
            w.Line();
        }
    }

    /// <summary>
    /// Gets the enum types from the engine assembly.
    /// </summary>
    public static IReadOnlyList<Type> GetEnumTypes()
    {
        var enumNamespace = "ExecutionEngine.Enums";
        var allowed = new HashSet<string>(AllowedEnumNames, StringComparer.Ordinal);
        return typeof(ExecutionEngine.Enums.RuntimeType).Assembly
            .GetExportedTypes()
            .Where(t => t.IsEnum && t.Namespace == enumNamespace && allowed.Contains(t.Name))
            .OrderBy(t => Array.IndexOf(AllowedEnumNames, t.Name))
            .ToList();
    }
}
