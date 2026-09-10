// -----------------------------------------------------------------------
// <copyright file="ChatWorkstreamPlugin.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Plugins;

public sealed class ChatWorkstreamPlugin : IWorkstreamPlugin
{
    public WorkstreamPluginDescriptor Descriptor { get; } = new("chat", "Chat / Remote control", "story-spawn", 1, Array.AsReadOnly(new[] { 1 }));

    public ChatSettingsValidation ValidateSettings(int version, JsonElement settings)
    {
        if (version != 1) return new(false, "unsupported-plugin-settings-version");
        // Version 1 stores destinations and filters in subscriptions. No arbitrary settings or secrets.
        return settings.ValueKind == JsonValueKind.Object && !settings.EnumerateObject().Any()
            ? ChatSettingsValidation.Success : new(false, "invalid-plugin-settings");
    }
}