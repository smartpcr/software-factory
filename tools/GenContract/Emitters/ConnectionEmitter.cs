// -----------------------------------------------------------------------
// <copyright file="ConnectionEmitter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Reflection;
using ExecutionEngine.Workflow;

/// <summary>
/// Emits the NodeConnectionDto TypeScript interface.
/// </summary>
public static class ConnectionEmitter
{
    /// <summary>
    /// Emits the NodeConnectionDto interface.
    /// </summary>
    public static void Emit(TsWriter w)
    {
        w.Line("export interface NodeConnectionDto {");
        w.Indent();

        var props = SortOrder.AlphaByName(
            typeof(NodeConnection).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            p => CamelCase(p.Name));

        foreach (var prop in props)
        {
            var nullable = IsNullable(prop);
            var tsType = TypeMapping.Map(prop.PropertyType, prop.Name, "NodeConnection");
            var optional = nullable ? "?" : "";
            w.Line($"{CamelCase(prop.Name)}{optional}: {tsType};");
        }

        w.Dedent();
        w.Line("}");
        w.Line();
    }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool IsNullable(PropertyInfo prop)
    {
        if (Nullable.GetUnderlyingType(prop.PropertyType) != null)
        {
            return true;
        }

        var ctx = new NullabilityInfoContext();
        var info = ctx.Create(prop);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState == NullabilityState.Nullable;
    }
}
