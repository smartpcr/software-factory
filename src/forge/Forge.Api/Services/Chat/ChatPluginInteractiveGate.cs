// -----------------------------------------------------------------------
// <copyright file="ChatPluginInteractiveGate.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

/// <summary>
/// The Stage 2 durable action boundary and Stage 3 provider interactions are available.
/// Each subscription still requires provider capabilities, scope grants and explicit opt-in.
/// </summary>
public static class ChatPluginInteractiveGate
{
    public static bool IsAvailable => true;
    public static bool AvailableFor(ChatPluginOptions options) => IsAvailable && options.PluginsEnabled && !options.TelegramSlice.Enabled;
    public static string Reason => "Interactive chat requires an enabled provider and an authorized, interactive workstream subscription.";
    public static void RequireAvailable(ChatPluginOptions options)
    {
        if (!AvailableFor(options)) throw new InvalidOperationException(Reason);
    }
}
