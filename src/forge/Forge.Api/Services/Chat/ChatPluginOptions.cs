// -----------------------------------------------------------------------
// <copyright file="ChatPluginOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Plugins;
using Microsoft.Extensions.Options;

public sealed class ChatPluginOptions
{
    public const string SectionName = "Forge:Chat";
    /// <summary>Unset preserves the explicitly opted-in legacy switch. Plugin engine requires true.</summary>
    public bool? Enabled { get; set; }
    public string Engine { get; set; } = "legacy";
    /// <summary>Stable identity in host configuration, independent of product/connection selection.</summary>
    public string HostId { get; set; } = "local";
    public TelegramSliceOptions TelegramSlice { get; set; } = new();
    public bool LegacyEnabled(bool legacyOptIn) => this.Engine == "legacy" && this.Enabled != false && legacyOptIn;
    public bool PluginsEnabled => this.Engine == "plugins" && this.Enabled == true;
    public string? UnavailableReason => this.Engine is not ("legacy" or "plugins") ? "unsupported-chat-engine"
        : string.IsNullOrWhiteSpace(this.HostId) ? "host-id-required" : null;
}

public static class ChatPluginRegistration
{
    public static IServiceCollection AddChatPlugins(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChatPluginOptions>(configuration.GetSection(ChatPluginOptions.SectionName));
        services.AddSingleton<IChatProviderRegistry, ChatProviderRegistry>();
        services.AddSingleton<IWorkstreamPlugin, ChatWorkstreamPlugin>();
        services.AddSingleton<WorkstreamPluginRegistry>();
        services.AddScoped<ChatConfigurationService>();
        services.AddHttpClient(TelegramNotificationProvider.ClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IChatProviderPlugin, TelegramNotificationProvider>();
        services.AddSingleton<ChatTestEnvironmentSecrets>();
        services.AddSingleton<ChatSliceCoordinator>();
        services.AddSingleton<ChatContentPolicy>();
        services.AddScoped<ChatTelegramSliceService>();
        services.AddScoped<ChatNotifier>();
        services.AddScoped<IChatNotifier, ChatEngineNotifier>();
        services.AddScoped<ChatSliceRetention>();
        services.AddSingleton<ChatTelegramSliceWorker>();
        services.AddOptions<ChatBridgeOptions>().PostConfigure<IOptions<ChatPluginOptions>>((legacy, plugins) =>
            legacy.Enabled = plugins.Value.LegacyEnabled(legacy.Enabled));
        services.AddOptions<ChatTransportOptions>().PostConfigure<IOptions<ChatPluginOptions>>((legacy, plugins) =>
            legacy.Enabled = plugins.Value.LegacyEnabled(legacy.Enabled));
        return services;
    }
}