// -----------------------------------------------------------------------
// <copyright file="ChatEngineCoordinator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Engine leases drain before a journal commit. Producers share a separate gate with that commit.</summary>
public sealed class ChatEngineCoordinator(IServiceScopeFactory scopes, IOptions<ChatPluginOptions> options,
    IOptionsMonitor<ChatBridgeOptions> bridge, IOptionsMonitor<ChatTransportOptions> transport,
    ChatLegacySource source, ChatRuntimeState runtime) : IChatOperationGate
{
    private readonly object sync = new();
    private readonly SemaphoreSlim initialize = new(1, 1);
    private readonly AsyncLocal<bool> transitionWork = new();
    private readonly AsyncLocal<bool> continuing = new();
    private bool initialized;
    private bool transitioning;
    private int active;
    private TaskCompletionSource? drained;
    public SemaphoreSlim Commands { get; } = new(1, 1);
    public SemaphoreSlim Routing { get; } = new(1, 1);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (this.initialized) return;
        await this.initialize.WaitAsync(ct);
        try
        {
            if (this.initialized) return;
            using var scope = scopes.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var row = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is not null)
            {
                if (row.Owner == "legacy" && row.Phase == "legacy")
                {
                    var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == ChatMigrationService.ConnectionId, ct);
                    var references = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(connection.SecretReferencesJson);
                    var token = references?.GetValueOrDefault("bot-token") is { } reference
                        ? await scope.ServiceProvider.GetRequiredService<ChatSecretResolver>().ReadAsync(reference, ct) : null;
                    // Never fall back to a stale token after a rotation followed by rollback.
                    transport.CurrentValue.Telegram.BotToken = token ?? "";
                    transport.CurrentValue.TokenBlobPath = "";
                    if (token is null || !token.StartsWith(connection.InstallationId + ":", StringComparison.Ordinal))
                    {
                        this.Apply("legacy"); bridge.CurrentValue.Enabled = false; transport.CurrentValue.Enabled = false;
                        runtime.RecordHealth(connection.Id, "degraded", "legacy-credential-unavailable-or-mismatched", null, null);
                        this.initialized = true;
                        return;
                    }
                }
                this.Apply(row.Owner);
            }
            this.initialized = true;
        }
        finally { this.initialize.Release(); }
    }

    public async Task<IDisposable?> EnterAsync(string engine, CancellationToken ct = default)
    {
        await this.InitializeAsync(ct);
        lock (this.sync)
        {
            if (options.Value.Enabled == false || options.Value.Engine != engine
                || this.transitioning && !this.transitionWork.Value && !this.continuing.Value || !runtime.EnsureHostOwnership()) return null;
            this.active++;
            return new Lease(() =>
            {
                lock (this.sync) { if (--this.active == 0) this.drained?.TrySetResult(); }
            });
        }
    }

    public IDisposable ContinueLease()
    {
        var previous = this.continuing.Value;
        this.continuing.Value = true;
        return new Lease(() => this.continuing.Value = previous);
    }

    public async Task<IDisposable> QuiesceAsync(CancellationToken ct)
    {
        await this.InitializeAsync(ct);
        if (!runtime.EnsureHostOwnership()) throw new ChatConfigurationException("chat-host-owned-elsewhere", 409);
        Task wait;
        lock (this.sync)
        {
            this.transitioning = true;
            this.drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (this.active == 0) this.drained.TrySetResult();
            wait = this.drained.Task;
        }
        var lease = new Lease(() => { lock (this.sync) { this.transitioning = false; this.drained = null; } runtime.Signal(); });
        try { await wait.WaitAsync(TimeSpan.FromSeconds(45), ct); return lease; }
        catch { lease.Dispose(); throw new ChatConfigurationException("chat-operations-still-running", 409); }
    }

    internal async Task DuringTransitionAsync(Func<Task> action)
    {
        this.transitionWork.Value = true;
        try { await action(); }
        finally { this.transitionWork.Value = false; }
    }

    internal void Apply(string owner)
    {
        options.Value.Engine = owner;
        // A selected migration is an explicit opt-in; an explicit host master-off still wins.
        if (options.Value.Enabled is null) options.Value.Enabled = true;
        bridge.CurrentValue.Enabled = owner == "legacy" && options.Value.Enabled != false && source.Bridge.Enabled;
        transport.CurrentValue.Enabled = bridge.CurrentValue.Enabled;
        runtime.Signal();
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref this.release, null)?.Invoke();
    }
}