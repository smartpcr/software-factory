// -----------------------------------------------------------------------
// <copyright file="CardMapper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Round-trip between <see cref="CardRow"/> and <see cref="Card"/>. Used by
/// the state endpoint and the cards endpoints; lifted out of
/// <c>StateEndpoints</c> so write-paths can reuse it.
/// </summary>
public static class CardMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Card ToContract(CardRow r) => new(
        r.Id, r.Epic, r.Col, r.Type, r.Title,
        Deserialize<List<string>>(r.TagsJson) ?? new(),
        r.Pts, r.Priority,
        r.AgentJson is null ? null : Deserialize<Agent>(r.AgentJson),
        r.Cost, r.AutoNext, r.Live, r.Progress, r.Branch,
        r.HumanGate, r.GateReason, r.Blocked, r.BlockedReason, r.DoneAt);

    public static CardRow ApplyContract(CardRow row, Card card)
    {
        row.Epic = card.Epic;
        row.Col = card.Col;
        row.Type = card.Type;
        row.Title = card.Title;
        row.TagsJson = Serialize(card.Tags);
        row.Pts = card.Pts;
        row.Priority = card.Priority;
        row.AgentJson = card.Agent is null ? null : Serialize(card.Agent);
        row.Cost = card.Cost;
        row.AutoNext = card.AutoNext;
        row.Live = card.Live;
        row.Progress = card.Progress;
        row.Branch = card.Branch;
        row.HumanGate = card.HumanGate;
        row.GateReason = card.GateReason;
        row.Blocked = card.Blocked;
        row.BlockedReason = card.BlockedReason;
        row.DoneAt = card.DoneAt;
        return row;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOpts);
}
