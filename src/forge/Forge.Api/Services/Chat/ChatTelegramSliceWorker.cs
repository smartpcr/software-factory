// -----------------------------------------------------------------------
// <copyright file="ChatTelegramSliceWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Options;

public sealed class ChatTelegramSliceWorker(IServiceScopeFactory scopes, IOptions<ChatPluginOptions> options,
    ILogger<ChatTelegramSliceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (options.Value.PluginsEnabled && options.Value.TelegramSlice.Enabled)
                {
                    var slice = scope.ServiceProvider.GetRequiredService<ChatTelegramSliceService>();
                    await slice.RunOutboundOnceAsync(stoppingToken);
                }
                if (DateTimeOffset.UtcNow >= nextRetention)
                {
                    await scope.ServiceProvider.GetRequiredService<ChatSliceRetention>().SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
                    nextRetention = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Telegram notification tick failed; row status will be rechecked. Provider details suppressed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}