// -----------------------------------------------------------------------
// <copyright file="PolicyEmitter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Reflection;

/// <summary>
/// Emits TypeScript interfaces for RetryPolicyDto and CircuitBreakerPolicyDto.
/// </summary>
public static class PolicyEmitter
{
    /// <summary>
    /// Emits the policy DTO interfaces.
    /// </summary>
    public static void Emit(TsWriter w)
    {
        EmitInterface(w, typeof(ExecutionEngine.Policies.RetryPolicy), "RetryPolicyDto");
        EmitInterface(w, typeof(ExecutionEngine.Policies.CircuitBreakerPolicy), "CircuitBreakerPolicyDto");
    }

    private static void EmitInterface(TsWriter w, Type clrType, string tsName)
    {
        w.Line($"export interface {tsName} {{");
        w.Indent();

        var props = SortOrder.AlphaByName(
            clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            p => CamelCase(p.Name));

        foreach (var prop in props)
        {
            var nullable = IsNullable(prop);
            var tsType = TypeMapping.Map(prop.PropertyType, prop.Name, clrType.Name);
            var optional = nullable ? "?" : "";
            var comment = prop.PropertyType == typeof(TimeSpan) ? " // ISO-8601 duration" : "";
            w.Line($"{CamelCase(prop.Name)}{optional}: {tsType};{comment}");
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
        // Check for Nullable<T> (value types)
        if (Nullable.GetUnderlyingType(prop.PropertyType) != null)
        {
            return true;
        }

        // Check NullabilityInfo for reference types
        var ctx = new NullabilityInfoContext();
        var info = ctx.Create(prop);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState == NullabilityState.Nullable;
    }
}
