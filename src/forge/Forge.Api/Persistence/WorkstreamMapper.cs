// -----------------------------------------------------------------------
// <copyright file="WorkstreamMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Forge.Api.Contracts;

public static class WorkstreamMapper
{
    public static Workstream ToContract(WorkstreamRow r) => new(
        r.Id, r.Name, r.Branch, r.Base, r.Epic, r.Status,
        CardMapper.Deserialize<List<string>>(r.WorktreeJson) ?? new(),
        r.Created, r.LastActive,
        CardMapper.Deserialize<List<string>>(r.AgentsJson) ?? new(),
        CardMapper.Deserialize<List<string>>(r.PrsJson) ?? new(),
        CardMapper.Deserialize<List<string>>(r.CardsJson) ?? new(),
        r.Commits, r.Ahead, r.Behind, r.Conflicts, r.Summary,
        r.BarStart, r.BarEnd, r.Planned,
        r.RepoId,
        r.StallReason);
}
