// -----------------------------------------------------------------------
// <copyright file="ChatConnectionSupervisor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class ChatSecretResolver(IEnumerable<IChatSecretStore> stores) : IChatSecretStore
{
    public string Id => "resolver";
    public ChatSecretStoreStatus Status => new(true, true);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
    {
        var matches = stores.Where(s => s.Id == reference.Store).ToArray();
        return matches.Length == 1 ? matches[0].ReadAsync(reference, ct) : ValueTask.FromResult<string?>(null);
    }
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException();
    public async Task<IReadOnlyList<string>> ValuesAsync(ChatConnectionRow row, CancellationToken ct)
    {
        var references = JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(row.SecretReferencesJson) ?? [];
        var values = new List<string>();
        foreach (var reference in references.Values)
            if (reference is not null && await this.ReadAsync(reference, ct) is { Length: > 0 } value) values.Add(value);
        return values;
    }
}

/// <summary>Immutable connection revisions; a reception key owns one receiver, including a shared Slack app socket.</summary>
public sealed class ChatConnectionSupervisor(IServiceScopeFactory scopes, IChatProviderRegistry providers, ChatSecretResolver secrets,
    ChatRuntimeState runtime, TimeProvider clock, IOptions<ChatPluginOptions> options) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receiver> receivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Provider, string Key)> retiredReceivers = new(StringComparer.Ordinal);
    private long lastHealthCheckpoint;
    private IServiceScope NewScope() => scopes.CreateScope();
    public sealed record Session(ChatConnectionRow Configuration, IChatConnectionSession Provider, CancellationTokenSource Cancellation);
    private sealed record Receiver(string ProviderId, string ReceptionKey, CancellationTokenSource Cancellation, Task Task);

    public async Task<Session?> GetAsync(ChatConnectionRow row, CancellationToken ct)
    {
        if (!runtime.EnsureHostOwnership() || !options.Value.PluginsEnabled || !providers.IsAvailable) return null;
        await this.gate.WaitAsync(ct);
        try
        {
            if (this.sessions.TryGetValue(row.Id, out var existing))
            {
                if (existing.Configuration.Revision == row.Revision) return existing;
                if (!await this.StopAsync(existing)) return null;
            }
            var plugin = providers.Resolve(row.ProviderId);
            if (plugin is null) return null;
            try
            {
                using var settings = JsonDocument.Parse(row.SettingsJson);
                if (!plugin.ValidateSettings(row.SettingsVersion, settings.RootElement).Valid) return null;
                var references = JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(row.SecretReferencesJson) ?? [];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var opened = await plugin.OpenSessionAsync(new(row.Id, row.ProviderId, row.InstallationId, row.SettingsVersion, row.Revision,
                    settings.RootElement.Clone(), references), secrets, timeout.Token);
                if (!opened.Succeeded || opened.Value!.ConnectionId != row.Id)
                {
                    if (opened.Value is not null) await opened.Value.DisposeAsync();
                    runtime.RecordHealth(row.Id, "degraded", SafeProviderReason(opened.Failure?.Code), null, null); return null;
                }
                var session = new Session(row, opened.Value, new CancellationTokenSource());
                this.sessions.Add(row.Id, session);
                runtime.RecordHealth(row.Id, "ready", null, null, null);
                return session;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { runtime.RecordHealth(row.Id, "degraded", "session-unavailable", null, null); return null; }
        }
        finally { this.gate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!runtime.EnsureHostOwnership()) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var rows = options.Value.PluginsEnabled && !options.Value.TelegramSlice.Enabled
            ? await db.ChatConnections.AsNoTracking().Where(c => c.Enabled && c.DeletedAt == null).ToListAsync(ct) : [];
        await this.gate.WaitAsync(ct);
        try
        {
            foreach (var stale in await db.ChatReceiverStates.Where(r => r.OwnerProcessInstanceId != runtime.ProcessId && r.RetiredAt == null).Take(100).ToListAsync(ct))
            {
                // The named mutex proves that the former process no longer owns reception.
                stale.State = "stopped"; stale.RetiredAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                stale.OwnerProcessInstanceId = null; stale.RecoveryDeadline = null;
            }
            foreach (var stale in await db.ChatConnectionStates.Where(s => s.RetiredAt == null
                && !db.ChatConnections.Any(c => c.Id == s.ConnectionId && c.DeletedAt == null)).Take(100).ToListAsync(ct))
            { stale.RetiredAt = clock.GetUtcNow().ToUnixTimeMilliseconds(); stale.Health = "stopped"; stale.Reason = "connection-retired"; }
            if (db.ChangeTracker.HasChanges()) await scope.ServiceProvider.GetRequiredService<ChatDbBudget>().SaveAsync(db, ct);
            foreach (var session in this.sessions.Values.ToArray())
                if (!rows.Any(r => r.Id == session.Configuration.Id && r.Revision == session.Configuration.Revision)) await this.StopAsync(session);
            foreach (var pair in this.receivers.Where(p => p.Value.Task.IsCompleted).ToArray())
            { pair.Value.Cancellation.Dispose(); this.receivers.Remove(pair.Key); }
        }
        finally { this.gate.Release(); }
        foreach (var row in rows) await this.GetAsync(row, ct);
        await this.gate.WaitAsync(ct);
        try
        {
            foreach (var group in this.sessions.Values.Where(s => rows.Any(r => r.Id == s.Configuration.Id && r.Revision == s.Configuration.Revision))
                .GroupBy(s => s.Configuration.ProviderId + ":" + s.Provider.ReceptionKey))
            {
                var first = group.First();
                var capability = providers.Resolve(first.Configuration.ProviderId)!.Descriptor.Capabilities;
                if (capability.Reception == ChatReceptionMode.None || this.receivers.ContainsKey(group.Key)) continue;
                if (group.Count() > 1 && (capability.Reception != ChatReceptionMode.Socket || first.Configuration.ProviderId != "slack"))
                {
                    foreach (var member in group) runtime.RecordHealth(member.Configuration.Id, "degraded", "duplicate-reception-key", null, null);
                    continue;
                }
                var state = await db.ChatConnectionStates.AsNoTracking().SingleOrDefaultAsync(s => s.ConnectionId == first.Configuration.Id, ct);
                if (first.Provider is IChatResumableSession resumable) await resumable.RestoreCursorAsync(state?.Cursor, ct);
                var ownership = await db.ChatReceiverStates.FindAsync([first.Configuration.ProviderId, first.Provider.ReceptionKey], ct);
                if (ownership is null)
                {
                    ownership = new() { ProviderId = first.Configuration.ProviderId, ReceptionKey = first.Provider.ReceptionKey };
                    db.ChatReceiverStates.Add(ownership);
                }
                ownership.OwnerProcessInstanceId = runtime.ProcessId; ownership.State = "receiving";
                ownership.RecoveryDeadline = clock.GetUtcNow().AddSeconds(60).ToUnixTimeMilliseconds(); ownership.RetiredAt = null;
                await scope.ServiceProvider.GetRequiredService<ChatDbBudget>().SaveAsync(db, ct);
                this.retiredReceivers.Remove(group.Key);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(first.Cancellation.Token);
                var key = group.Key;
                var task = Task.Run(async () =>
                {
                    try { await first.Provider.ReceiveAsync(new ReceptionSink(this, key), cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { runtime.RecordHealth(first.Configuration.Id, "degraded", SafeProviderReason(ex is InvalidOperationException ? ex.Message : null), null, null); }
                }, CancellationToken.None);
                this.receivers[key] = new(first.Configuration.ProviderId, first.Provider.ReceptionKey, cancellation, task);
            }
        }
        finally { this.gate.Release(); }
        await this.CheckpointHealthAsync(ct);
    }

    public async Task CheckpointHealthAsync(CancellationToken ct)
    {
        await this.gate.WaitAsync(ct);
        try { await this.CheckpointHealthCoreAsync(ct); }
        finally { this.gate.Release(); }
    }

    internal static string SafeProviderReason(string? code) => code switch
    {
        "telegram-webhook-conflict" or "duplicate-telegram-bot" or "telegram-receiver-conflict" or "telegram-token-revoked"
            or "telegram-token-required" or "telegram-secret-unavailable" or "telegram-identity-mismatch" or "telegram-membership-denied"
            or "telegram-rate-limited" => code,
        _ => "session-unavailable",
    };

    private async Task CheckpointHealthCoreAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (now - this.lastHealthCheckpoint < 60000 || runtime.Health.IsEmpty && this.receivers.Count == 0 && this.retiredReceivers.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var health = runtime.Health.ToArray();
        foreach (var pair in health)
        {
            var row = await db.ChatConnectionStates.SingleOrDefaultAsync(s => s.ConnectionId == pair.Key, ct);
            if (row is null) { row = new() { ConnectionId = pair.Key }; db.ChatConnectionStates.Add(row); }
            row.Health = pair.Value.Health; row.Reason = pair.Value.Reason;
            row.LastSendAt = pair.Value.Sent ?? row.LastSendAt; row.LastReceiveAt = pair.Value.Received ?? row.LastReceiveAt;
            var connection = await db.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == pair.Key, ct);
            row.SettingsRevision = connection?.Revision ?? row.SettingsRevision;
            row.RetiredAt = connection?.DeletedAt?.ToUnixTimeMilliseconds();
        }
        foreach (var receiver in this.receivers.Values)
        {
            var row = await db.ChatReceiverStates.FindAsync([receiver.ProviderId, receiver.ReceptionKey], ct);
            if (row is null) { row = new() { ProviderId = receiver.ProviderId, ReceptionKey = receiver.ReceptionKey }; db.ChatReceiverStates.Add(row); }
            row.OwnerProcessInstanceId = runtime.ProcessId; row.State = "receiving"; row.CheckpointAt = now; row.RecoveryDeadline = now + 60000; row.RetiredAt = null;
        }
        foreach (var retired in this.retiredReceivers.Values)
        {
            var row = await db.ChatReceiverStates.FindAsync([retired.Provider, retired.Key], ct);
            if (row is not null) { row.State = "stopped"; row.RetiredAt = now; row.OwnerProcessInstanceId = null; row.RecoveryDeadline = null; }
        }
        if (db.ChangeTracker.HasChanges()) await scope.ServiceProvider.GetRequiredService<ChatDbBudget>().SaveAsync(db, ct);
        this.retiredReceivers.Clear();
        foreach (var entry in health)
            ((ICollection<KeyValuePair<string, (string Health, string? Reason, long? Sent, long? Received)>>)runtime.Health).Remove(entry);
        this.lastHealthCheckpoint = now;
    }

    private async Task<bool> StopAsync(Session session)
    {
        var key = session.Configuration.ProviderId + ":" + session.Provider.ReceptionKey;
        if (this.receivers.TryGetValue(key, out var receiver))
        {
            receiver.Cancellation.Cancel();
            try { await receiver.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                runtime.RecordHealth(session.Configuration.Id, "degraded", "receiver-stop-pending", null, null);
                return false;
            }
            this.receivers.Remove(key);
            this.retiredReceivers[key] = (receiver.ProviderId, receiver.ReceptionKey);
            receiver.Cancellation.Dispose();
        }
        session.Cancellation.Cancel();
        await session.Provider.DisposeAsync(); session.Cancellation.Dispose(); this.sessions.Remove(session.Configuration.Id);
        runtime.RecordHealth(session.Configuration.Id, "stopped", "configuration-changed", null, null);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync();
        try { foreach (var session in this.sessions.Values.ToArray()) await this.StopAsync(session); }
        finally { this.gate.Release(); }
    }
    public void Dispose() => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class ReceptionSink(ChatConnectionSupervisor supervisor, string receptionKey) : IChatDurableBatchSink
    {
        public ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default)
            => this.AcceptBatchAsync([envelope], [], ct);
        public async ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes, IReadOnlyList<ChatAcceptedCursor> cursors, CancellationToken ct = default)
        {
            var routed = new List<ChatInboundEnvelope>();
            var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
            await supervisor.gate.WaitAsync(ct);
            try
            {
                foreach (var envelope in envelopes)
                {
                    var candidates = supervisor.sessions.Values.Where(s => s.Configuration.ProviderId + ":" + s.Provider.ReceptionKey == receptionKey
                        && s.Configuration.ProviderId == envelope.ProviderId && (envelope.ProviderId == "slack"
                            ? s.Configuration.InstallationId == envelope.Address.ExternalTenantId : s.Configuration.Id == envelope.ConnectionId)).ToArray();
                    if (candidates.Length != 1) return ChatInboundAcceptance.Retry;
                    var connection = candidates[0].Configuration;
                    routed.Add(envelope with { ConnectionId = connection.Id, Address = envelope.Address with { ConnectionId = connection.Id } });
                    revisions[connection.Id] = connection.Revision;
                }
            }
            finally { supervisor.gate.Release(); }
            using var scope = supervisor.NewScope();
            return await scope.ServiceProvider.GetRequiredService<ChatInboxService>().AcceptBatchAsync(routed, cursors, revisions, ct);
        }
    }
}