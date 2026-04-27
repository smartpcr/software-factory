// -----------------------------------------------------------------------
// <copyright file="SprintMappers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Phase 15 — Row → Contract mapping for product iterations, area paths,
/// repo bindings, and saved prefixes. Bindings + work-item counts are
/// supplied by the caller because they require a join the row alone can't
/// provide.
/// </summary>
public static class SprintMappers
{
    public static ProductIteration MapIteration(
        ProductIterationRow row,
        IReadOnlyList<IterationBinding> bindings,
        int pointsTotal = 0,
        int pointsDone = 0)
        => new(
            row.Id,
            row.ProductId,
            row.ProviderKind,
            row.ProviderOrg,
            row.ProviderProject,
            row.ProviderNodeId,
            row.Path,
            row.Name,
            row.StartDate,
            row.FinishDate,
            row.State,
            bindings,
            pointsTotal,
            pointsDone,
            row.ImportedAt,
            row.RefreshedAt);

    public static ProductAreaPath MapArea(ProductAreaPathRow row, int workItemCount)
        => new(
            row.Id,
            row.ProductId,
            row.ProviderKind,
            row.ProviderOrg,
            row.ProviderProject,
            row.ProviderNodeId,
            row.Path,
            row.ParentPath,
            row.ParentNodeId,
            row.IsDefaultFilter,
            workItemCount,
            row.ImportedAt);

    public static SavedPrefix MapSavedPrefix(ProductSavedPrefixRow row, string repoName)
    {
        SavedPrefixDelta delta;
        try
        {
            delta = JsonSerializer.Deserialize<SavedPrefixDelta>(row.LastDeltaJson, JsonOpts) ?? Empty;
        }
        catch
        {
            delta = Empty;
        }
        return new SavedPrefix(
            row.Id.ToString(),
            row.ProductId,
            row.Prefix,
            row.Kind,
            row.RepoId,
            repoName,
            row.AddedBy,
            row.LastRefreshedAt,
            delta);
    }

    public static string SerializeDelta(SavedPrefixDelta d) => JsonSerializer.Serialize(d, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly SavedPrefixDelta Empty = new(0, 0, 0);
}
