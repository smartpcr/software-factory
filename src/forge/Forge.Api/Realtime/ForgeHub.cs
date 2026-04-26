// -----------------------------------------------------------------------
// <copyright file="ForgeHub.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Realtime;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// Realtime hub for the Forge UI. Phase 1 emits one event:
/// <c>card.changed</c> with payload <c>{ kind: "created"|"updated"|"moved", card }</c>.
/// Subscribers join no groups today; per-board / per-user filtering arrives later.
/// </summary>
public class ForgeHub : Hub
{
}
