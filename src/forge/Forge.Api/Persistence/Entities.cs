// -----------------------------------------------------------------------
// <copyright file="Entities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>
/// Persistence entities for the Forge cache. Phase-0 keeps wide JSON columns
/// for the "shape" fields (tags, crew, agent, build, review, etc.) so the
/// prototype data round-trips without an over-eager normalisation. Phase 1+
/// will narrow these once the write-paths solidify.
/// </summary>
public class EpicRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int Progress { get; set; }

    public string CrewJson { get; set; } = "[]";

    public double Cost { get; set; }

    public string Color { get; set; } = string.Empty;
}

public class CardRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Epic { get; set; } = string.Empty;

    public string Col { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string TagsJson { get; set; } = "[]";

    public int Pts { get; set; }

    public string Priority { get; set; } = string.Empty;

    public string? AgentJson { get; set; }

    public double Cost { get; set; }

    public string? AutoNext { get; set; }

    public bool Live { get; set; }

    public int? Progress { get; set; }

    public string? Branch { get; set; }

    public bool HumanGate { get; set; }

    public string? GateReason { get; set; }

    public bool Blocked { get; set; }

    public string? BlockedReason { get; set; }

    public string? DoneAt { get; set; }
}

public class ColumnRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Sort { get; set; }

    public int? WipLimit { get; set; }
}

public class WorkstreamRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;

    public string Epic { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string WorktreeJson { get; set; } = "[]";

    public double Created { get; set; }

    public double LastActive { get; set; }

    public string AgentsJson { get; set; } = "[]";

    public string PrsJson { get; set; } = "[]";

    public string CardsJson { get; set; } = "[]";

    public int Commits { get; set; }

    public int Ahead { get; set; }

    public int Behind { get; set; }

    public int Conflicts { get; set; }

    public string Summary { get; set; } = string.Empty;

    public double BarStart { get; set; }

    public double BarEnd { get; set; }

    public bool Planned { get; set; }
}

public class WsEventRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Ws { get; set; } = string.Empty;

    public double Day { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

public class PrRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Col { get; set; } = string.Empty;

    public string Epic { get; set; } = string.Empty;

    public string EpicColor { get; set; } = string.Empty;

    public string Workstream { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string AuthorJson { get; set; } = "{}";

    public int Additions { get; set; }

    public int Deletions { get; set; }

    public int Files { get; set; }

    public int Commits { get; set; }

    public string Age { get; set; } = string.Empty;

    public string? BuildJson { get; set; }

    public string? ReviewJson { get; set; }

    public bool Conflicts { get; set; }

    public bool HumanGate { get; set; }

    public string? GateReason { get; set; }

    public string? ConflictPaths { get; set; }

    public bool AutoMerge { get; set; }

    public bool Blocked { get; set; }

    public string? ImplJson { get; set; }
}

public class WsStatRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Val { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public string Trend { get; set; } = string.Empty;

    public string TrendVal { get; set; } = string.Empty;
}

public class OverviewActivityRow
{
    public int Id { get; set; }

    public int Sort { get; set; }

    public string Time { get; set; } = string.Empty;

    public string Who { get; set; } = string.Empty;

    public string Msg { get; set; } = string.Empty;
}

public class OverviewAgentRow
{
    public string Id { get; set; } = string.Empty;

    public int Sort { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Task { get; set; } = string.Empty;
}
