// -----------------------------------------------------------------------
// <copyright file="ChatServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI helpers so a host app subscribes to a chat provider + channel in a few
/// lines. Typical wiring:
/// <code>
/// services.Configure&lt;ChatTransportOptions&gt;(config.GetSection("MyApp:ChatBridge"));
/// services.AddTelegramChatTransport();
/// services.AddChatChannelMap&lt;MyChannelStore&gt;();      // route ⇄ channel mapping
/// services.AddChatInboundHandler&lt;MyInboundHandler&gt;(); // receive
/// services.AddSingleton&lt;IChatCursorStore, MyCursorStore&gt;(); // or InMemoryChatCursorStore
/// services.AddHostedService&lt;ChatInboundPump&gt;();      // long-poll loop
/// </code>
/// Sending is just <see cref="IChatTransport.SendAsync"/> (optionally via
/// <see cref="IChatChannelMap.EnsureChannelAsync"/> for per-route topics).
/// </summary>
public static class ChatServiceCollectionExtensions
{
    /// <summary>Register the Telegram transport (+ its named HTTP client) as the <see cref="IChatTransport"/>.</summary>
    public static IServiceCollection AddTelegramChatTransport(this IServiceCollection services)
    {
        services.AddHttpClient(TelegramTransport.ClientName);
        services.TryAddSingleton<IChatTransport, TelegramTransport>();
        return services;
    }

    /// <summary>Register the route⇄channel map over the app's <typeparamref name="TStore"/> (scoped — stores are usually DB-backed).</summary>
    public static IServiceCollection AddChatChannelMap<TStore>(this IServiceCollection services)
        where TStore : class, IChatChannelStore
    {
        services.TryAddScoped<IChatChannelStore, TStore>();
        services.TryAddScoped<IChatChannelMap, ChatChannelMap>();
        return services;
    }

    /// <summary>Subscribe an inbound handler; every handler receives every update (scoped per update).</summary>
    public static IServiceCollection AddChatInboundHandler<THandler>(this IServiceCollection services)
        where THandler : class, IChatInboundHandler
    {
        services.AddScoped<IChatInboundHandler, THandler>();
        return services;
    }
}
