// -----------------------------------------------------------------------
// <copyright file="SortOrder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Deterministic ordering helpers for emitted symbols.
/// </summary>
public static class SortOrder
{
    /// <summary>
    /// Orders properties alphabetically by name using ordinal comparison.
    /// </summary>
    public static IOrderedEnumerable<T> AlphaByName<T>(IEnumerable<T> props, Func<T, string> nameSelector)
    {
        return props.OrderBy(nameSelector, StringComparer.Ordinal);
    }
}
