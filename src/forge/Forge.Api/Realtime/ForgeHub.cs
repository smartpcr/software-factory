// -----------------------------------------------------------------------
// <copyright file="ForgeHub.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Realtime;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Realtime hub for the Forge UI.
///
/// Phase 1 broadcasts <c>card.changed</c> with payload
/// <c>{ kind: "created"|"updated"|"moved", card }</c> to all subscribers.
///
/// Phase 11.7 introduces per-agent groups: clients call
/// <see cref="Subscribe"/> with <c>"agent.{agentId}"</c> to receive
/// <c>agent.event</c> token / status messages and <c>agent.usage</c> on finish.
/// </summary>
public class ForgeHub : Hub
{
    /// <summary>Join a named group (e.g. <c>agent.E4</c>) so server-side fan-out reaches this connection.</summary>
    public Task Subscribe(string channel) => this.Groups.AddToGroupAsync(this.Context.ConnectionId, channel);

    /// <summary>Leave a previously-joined group.</summary>
    public Task Unsubscribe(string channel) => this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, channel);
}
