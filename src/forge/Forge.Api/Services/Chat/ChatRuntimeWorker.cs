// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeWorker.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Options;

public sealed class ChatRuntimeWorker(IServiceScopeFactory scopes, ChatRuntimeState runtime, ChatConnectionSupervisor supervisor,
    TimeProvider clock, IOptions<ChatPluginOptions> options, ILogger<ChatRuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconcile = DateTimeOffset.MinValue; var retention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                if (now >= retention && runtime.EnsureHostOwnership())
                {
                    using var cleanup = scopes.CreateScope();
                    await cleanup.ServiceProvider.GetRequiredService<ChatRetentionService>().RunOnceAsync(stoppingToken);
                    retention = now.AddHours(1);
                }
                if (options.Value.Engine == "plugins" && !options.Value.TelegramSlice.Enabled && runtime.EnsureHostOwnership())
                {
                    using var scope = scopes.CreateScope(); var services = scope.ServiceProvider;
                    await supervisor.RefreshAsync(stoppingToken);
                    if (now >= reconcile) { await services.GetRequiredService<ChatScopeReconciler>().RunOnceAsync(stoppingToken); reconcile = now.AddMinutes(5); }
                    await services.GetRequiredService<ChatInboxService>().DispatchOnceAsync(stoppingToken);
                    await services.GetRequiredService<ChatResultReconciler>().RunOnceAsync(stoppingToken);
                    await services.GetRequiredService<ChatDeliveryService>().RunOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Chat runtime work paused; diagnostic state records the safe failure category."); }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); wait.CancelAfter(TimeSpan.FromSeconds(5));
            try { await runtime.Wake.Reader.ReadAsync(wait.Token); } catch (OperationCanceledException) { }
        }
    }
}