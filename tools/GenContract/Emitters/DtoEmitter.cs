// -----------------------------------------------------------------------
// <copyright file="DtoEmitter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExecutionEngine.Nodes.Definitions;

/// <summary>
/// Emits TypeScript interfaces for NodeDtoBase and per-kind node definitions,
/// plus the discriminated union NodeDto.
/// </summary>
public static class DtoEmitter
{
    /// <summary>
    /// Emits all node DTO interfaces and the NodeDto union.
    /// </summary>
    public static void Emit(TsWriter w)
    {
        // Emit base interface
        EmitBase(w);

        // Get all concrete node definition types
        var kinds = GetKindTypes();

        // Emit per-kind interfaces
        foreach (var kind in kinds)
        {
            EmitKind(w, kind);
        }

        // Emit discriminated union
        w.Line("export type NodeDto =");
        w.Indent();
        for (var i = 0; i < kinds.Count; i++)
        {
            var kindName = GetDtoName(kinds[i]);
            var sep = i < kinds.Count - 1 ? " |" : ";";
            w.Line($"| {kindName}{(i < kinds.Count - 1 ? "" : ";")}");
        }

        w.Dedent();
        w.Line();
    }

    /// <summary>
    /// Gets all concrete node definition types, ordered alphabetically.
    /// </summary>
    public static IReadOnlyList<Type> GetKindTypes()
    {
        return typeof(NodeDefinition).Assembly
            .GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(NodeDefinition)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void EmitBase(TsWriter w)
    {
        w.Line("export interface NodeDtoBase {");
        w.Indent();

        // Emit the discriminator first
        w.Line("runtimeType: RuntimeType;");

        var props = SortOrder.AlphaByName(
            GetBaseProperties(),
            p => CamelCase(p.Name));

        foreach (var prop in props)
        {
            // Skip RuntimeType — already emitted as discriminator
            if (prop.Name == "RuntimeType")
            {
                continue;
            }

            var nullable = IsNullable(prop);
            var tsType = TypeMapping.Map(prop.PropertyType, prop.Name, "NodeDefinition");
            var optional = nullable ? "?" : "";
            var comment = prop.PropertyType == typeof(TimeSpan) ? " // ISO-8601 duration" : "";
            w.Line($"{CamelCase(prop.Name)}{optional}: {tsType};{comment}");
        }

        w.Dedent();
        w.Line("}");
        w.Line();
    }

    private static void EmitKind(TsWriter w, Type kindType)
    {
        var dtoName = GetDtoName(kindType);
        var runtimeTypeValue = GetRuntimeTypeValue(kindType);

        w.Line($"export interface {dtoName} extends NodeDtoBase {{");
        w.Indent();

        // Discriminator override with literal type
        w.Line($"runtimeType: '{runtimeTypeValue}';");

        var declaredProps = SortOrder.AlphaByName(
            kindType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            p => CamelCase(p.Name));

        foreach (var prop in declaredProps)
        {
            // Skip RuntimeType — already emitted as discriminator
            if (prop.Name == "RuntimeType")
            {
                continue;
            }

            var nullable = IsNullable(prop);
            var tsType = TypeMapping.Map(prop.PropertyType, prop.Name, kindType.Name);
            var optional = nullable ? "?" : "";
            var comment = prop.PropertyType == typeof(TimeSpan) ? " // ISO-8601 duration" : "";
            w.Line($"{CamelCase(prop.Name)}{optional}: {tsType};{comment}");
        }

        w.Dedent();
        w.Line("}");
        w.Line();
    }

    private static IReadOnlyList<PropertyInfo> GetBaseProperties()
    {
        // Get properties declared on NodeDefinition itself (not inherited)
        return typeof(NodeDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();
    }

    private static string GetDtoName(Type kindType)
    {
        var name = kindType.Name;
        // Remove "Definition" suffix and add "Dto"
        if (name.EndsWith("Definition", StringComparison.Ordinal))
        {
            name = name[..^"Definition".Length];
        }

        // Special case: keep "Node" suffix for clarity
        if (!name.EndsWith("Node", StringComparison.Ordinal))
        {
            name += "Node";
        }

        return name + "Dto";
    }

    private static string GetRuntimeTypeValue(Type kindType)
    {
        // Create an instance to read the RuntimeType property
        try
        {
            var instance = Activator.CreateInstance(kindType);
            var prop = kindType.GetProperty("RuntimeType")!;
            var val = prop.GetValue(instance);
            return val?.ToString() ?? kindType.Name.Replace("NodeDefinition", "");
        }
        catch
        {
            // Fallback: derive from class name
            var name = kindType.Name.Replace("NodeDefinition", "");
            return name;
        }
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
