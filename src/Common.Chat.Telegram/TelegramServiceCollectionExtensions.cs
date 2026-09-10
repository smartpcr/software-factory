// -----------------------------------------------------------------------
// <copyright file="TelegramServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class TelegramServiceCollectionExtensions
{
    /// <summary>Compatibility registration for the legacy engine; protocol code lives in Common.Chat.Telegram.</summary>
    public static IServiceCollection AddTelegramChatTransport(this IServiceCollection services)
    {
        services.AddHttpClient(TelegramTransport.ClientName).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton<IChatTransport, TelegramTransport>();
        return services;
    }
}