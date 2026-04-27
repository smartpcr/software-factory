// -----------------------------------------------------------------------
// <copyright file="IModelRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using Forge.Api.Contracts;

/// <summary>
/// Phase 11.2 — read-only catalog of LLM models Forge knows about.
/// Backed by <c>Agents/Models/models.json</c> shipped with the API.
/// </summary>
public interface IModelRegistry
{
    /// <summary>All known models in the order they appear in the JSON seed.</summary>
    IReadOnlyList<ModelInfo> All { get; }

    /// <summary>Returns the model with the given <paramref name="id"/>, or null if not in the registry.</summary>
    ModelInfo? Find(string id);
}
