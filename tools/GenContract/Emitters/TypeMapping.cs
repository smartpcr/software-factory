// -----------------------------------------------------------------------
// <copyright file="TypeMapping.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Collections.Generic;

/// <summary>
/// Maps C# CLR types to TypeScript type strings.
/// </summary>
public static class TypeMapping
{
    private static readonly HashSet<string> KnownEnumNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers enum type names so they can be mapped to their TS union name.
    /// </summary>
    public static void RegisterEnumName(string name)
    {
        KnownEnumNames.Add(name);
    }

    /// <summary>
    /// Maps a CLR type to a TypeScript type string.
    /// </summary>
    /// <param name="clrType">The CLR type to map.</param>
    /// <param name="propertyName">The property name for error context.</param>
    /// <param name="ownerTypeName">The owner type name for error context.</param>
    /// <returns>The TypeScript type string.</returns>
    public static string Map(Type clrType, string propertyName, string ownerTypeName)
    {
        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(clrType);
        if (underlying != null)
        {
            return Map(underlying, propertyName, ownerTypeName);
        }

        if (clrType == typeof(string))
        {
            return "string";
        }

        if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short)
            || clrType == typeof(byte) || clrType == typeof(float) || clrType == typeof(double)
            || clrType == typeof(decimal))
        {
            return "number";
        }

        if (clrType == typeof(bool))
        {
            return "boolean";
        }

        if (clrType == typeof(TimeSpan))
        {
            return "string"; // ISO-8601 duration format
        }

        if (clrType.IsEnum)
        {
            // Parity enums (registered via EnumEmitter) are referenced by name.
            // Non-parity enums (e.g. RetryStrategy) are inlined as string-literal unions
            // so the generated file contains exactly the 7 parity export-type unions.
            if (KnownEnumNames.Contains(clrType.Name))
            {
                return clrType.Name;
            }

            var members = Enum.GetValues(clrType)
                .Cast<object>()
                .Select(v => $"'{Enum.GetName(clrType, v)}'");
            return string.Join(" | ", members);
        }

        // Dictionary<K,V> -> Record<K,V>
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = clrType.GetGenericArguments();
            var keyTs = Map(args[0], propertyName, ownerTypeName);
            var valueTs = Map(args[1], propertyName, ownerTypeName);
            return $"Record<{keyTs}, {valueTs}>";
        }

        // List<T> -> T[]
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elemType = clrType.GetGenericArguments()[0];

            // List<Type> -> string[] (FQTNs per spec)
            if (elemType == typeof(Type))
            {
                return "string[]";
            }

            var elemTs = Map(elemType, propertyName, ownerTypeName);
            return $"{elemTs}[]";
        }

        // Known complex types that we represent as nested DTOs
        if (clrType.Name == "WorkflowDefinition")
        {
            return "WorkflowDefinitionDto";
        }

        if (clrType.Name == "NodeDefinition")
        {
            return "NodeDto";
        }

        if (clrType.Name == "NodeConnection")
        {
            return "NodeConnectionDto";
        }

        if (clrType.Name == "RetryPolicy")
        {
            return "RetryPolicyDto";
        }

        if (clrType.Name == "CircuitBreakerPolicy")
        {
            return "CircuitBreakerPolicyDto";
        }

        // object / generic
        if (clrType == typeof(object))
        {
            return "unknown";
        }

        throw new InvalidOperationException(
            $"Unsupported CLR type '{clrType.FullName}' on property '{ownerTypeName}.{propertyName}'. " +
            "Add an explicit mapping in TypeMapping.cs.");
    }
}
