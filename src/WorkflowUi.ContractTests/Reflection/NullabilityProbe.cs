// -----------------------------------------------------------------------
// <copyright file="NullabilityProbe.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests.Reflection;

using System.Reflection;

/// <summary>
/// Thin wrapper around NullabilityInfoContext to probe whether
/// a property is nullable. Requires the owning assembly to compile
/// with &lt;Nullable&gt;enable&lt;/Nullable&gt; (already true for ExecutionEngine).
/// </summary>
public static class NullabilityProbe
{
    private static readonly NullabilityInfoContext Context = new();

    /// <summary>
    /// Returns true if the property is nullable (reference type annotated ? or Nullable&lt;T&gt;).
    /// </summary>
    public static bool IsNullable(PropertyInfo property)
    {
        var info = Context.Create(property);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState == NullabilityState.Nullable;
    }
}
