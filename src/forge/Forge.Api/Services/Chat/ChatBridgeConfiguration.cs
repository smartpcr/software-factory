// -----------------------------------------------------------------------
// <copyright file="ChatBridgeConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

/// <summary>Host-user chat settings shared by direct starts and the normal launcher.</summary>
public static class ChatBridgeConfiguration
{
    public const string SettingsPathEnvironmentVariable = "FORGE_CHAT_SETTINGS_PATH";

    public static string DefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forge", "chat-settings.json");

    /// <summary>
    /// Insert host settings after application JSON (including Development/user secrets)
    /// but before environment, command-line, and test overrides. Missing settings are
    /// safe; invalid settings fail startup. Restart to load edits to this file.
    /// </summary>
    public static void AddHostConfiguration(IConfigurationBuilder configuration, string? settingsPath = null)
    {
        settingsPath ??= Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(settingsPath)) settingsPath = DefaultSettingsPath();
        if (!Path.IsPathFullyQualified(settingsPath))
        {
            throw new ArgumentException($"{SettingsPathEnvironmentVariable} must be an absolute path.", nameof(settingsPath));
        }

        var source = new JsonConfigurationSource
        {
            Path = settingsPath,
            Optional = true,
            ReloadOnChange = false,
        };
        source.ResolveFileProvider();

        var insertAt = 0;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource) insertAt = i + 1;
        }

        configuration.Sources.Insert(insertAt, source);
    }
}
