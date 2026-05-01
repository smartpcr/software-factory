// -----------------------------------------------------------------------
// <copyright file="PrMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Forge.Api.Contracts;

public static class PrMapper
{
    public static Pr ToContract(PrRow r, int? openCommentCount = null, int? attentionCommentCount = null) => new(
        r.Id, r.Col, r.Epic, r.EpicColor, r.Workstream, r.Base, r.Branch, r.Title,
        CardMapper.Deserialize<Agent>(r.AuthorJson) ?? new("", "", ""),
        r.Additions, r.Deletions, r.Files, r.Commits, r.Age,
        r.BuildJson is null ? null : CardMapper.Deserialize<BuildStatus>(r.BuildJson),
        r.ReviewJson is null ? null : CardMapper.Deserialize<ReviewVerdict>(r.ReviewJson),
        r.Conflicts, r.HumanGate, r.GateReason, r.ConflictPaths, r.AutoMerge, r.Blocked,
        r.ImplJson is null ? null : CardMapper.Deserialize<ImplStatus>(r.ImplJson),
        // Phase 28 — iteration count.
        Updates: r.Updates,
        // Phase 24 — sync state + comment counts.
        IsActive: r.IsActive,
        OpenCommentCount: openCommentCount,
        AttentionCommentCount: attentionCommentCount,
        LastSyncAt: r.LastSyncAt,
        LastSyncStatus: r.LastSyncStatus,
        Provider: r.Provider,
        WorkItemId: r.WorkItemId,
        // Phase 27 — detail enrichment.
        Description: string.IsNullOrEmpty(r.Description) ? null : r.Description,
        WebUrl: r.WebUrl,
        CreatedAt: r.CreatedAt,
        UpdatedAt: r.UpdatedAt);
}
