// -----------------------------------------------------------------------
// <copyright file="TelegramSliceOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

public sealed class TelegramSliceOptions
{
    public bool Enabled { get; set; }
    public bool NewTestBotConfirmed { get; set; }
    /// <summary>Must explicitly match this host's isolated state folder; the production default is refused.</summary>
    public string IsolatedProfileRoot { get; set; } = "";
    public string ForgeBaseUrl { get; set; } = "http://localhost:5181";
}

/// <summary>One process, one slice route. Stage 2 adds durable ownership and supervision.</summary>
public sealed class ChatSliceCoordinator : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public void Dispose() => this.Gate.Dispose();
}