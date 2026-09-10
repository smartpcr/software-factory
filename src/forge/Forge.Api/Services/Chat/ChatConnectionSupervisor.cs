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
    ChatRuntimeState runtime, TimeProvider clock, IOptions<ChatPluginOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receiver> receivers = new(StringComparer.Ordinal);
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
                await this.StopAsync(existing);
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
                if (!opened.Succeeded || opened.Value!.ConnectionId != row.Id) return null;
                var session = new Session(row, opened.Value, new CancellationTokenSource());
                this.sessions.Add(row.Id, session);
                runtime.Health[row.Id] = ("ready", null, null, null);
                return session;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { runtime.Health[row.Id] = ("degraded", "session-unavailable", null, null); return null; }
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
            foreach (var group in this.sessions.Values.GroupBy(s => s.Configuration.ProviderId + ":" + s.Provider.ReceptionKey))
            {
                var first = group.First();
                var capability = providers.Resolve(first.Configuration.ProviderId)!.Descriptor.Capabilities;
                if (capability.Reception == ChatReceptionMode.None || this.receivers.ContainsKey(group.Key)) continue;
                if (group.Count() > 1 && (capability.Reception != ChatReceptionMode.Socket || first.Configuration.ProviderId != "slack"))
                {
                    foreach (var member in group) runtime.Health[member.Configuration.Id] = ("degraded", "duplicate-reception-key", null, null);
                    continue;
                }
                var state = await db.ChatConnectionStates.AsNoTracking().SingleOrDefaultAsync(s => s.ConnectionId == first.Configuration.Id, ct);
                if (first.Provider is IChatResumableSession resumable) await resumable.RestoreCursorAsync(state?.Cursor, ct);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(first.Cancellation.Token);
                var key = group.Key;
                var task = Task.Run(async () =>
                {
                    try { await first.Provider.ReceiveAsync(new ReceptionSink(this, key), cancellation.Token); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch { runtime.Health[first.Configuration.Id] = ("degraded", "receiver-disconnected", null, null); }
                }, CancellationToken.None);
                this.receivers[key] = new(first.Configuration.ProviderId, first.Provider.ReceptionKey, cancellation, task);
            }
        }
        finally { this.gate.Release(); }
        await this.CheckpointHealthAsync(ct);
    }

    public async Task CheckpointHealthAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (now - this.lastHealthCheckpoint < 60000 || runtime.Health.IsEmpty && this.receivers.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        foreach (var pair in runtime.Health.ToArray())
        {
            var row = await db.ChatConnectionStates.SingleOrDefaultAsync(s => s.ConnectionId == pair.Key, ct);
            if (row is null) { row = new() { ConnectionId = pair.Key }; db.ChatConnectionStates.Add(row); }
            row.Health = pair.Value.Health; row.Reason = pair.Value.Reason;
            row.LastSendAt = pair.Value.Sent ?? row.LastSendAt; row.LastReceiveAt = pair.Value.Received ?? row.LastReceiveAt;
            runtime.Health.TryRemove(pair.Key, out _);
        }
        foreach (var receiver in this.receivers.Values)
        {
            var row = await db.ChatReceiverStates.FindAsync([receiver.ProviderId, receiver.ReceptionKey], ct);
            if (row is null) { row = new() { ProviderId = receiver.ProviderId, ReceptionKey = receiver.ReceptionKey }; db.ChatReceiverStates.Add(row); }
            row.OwnerProcessInstanceId = runtime.ProcessId; row.State = "receiving"; row.CheckpointAt = now; row.RecoveryDeadline = now + 60000; row.RetiredAt = null;
        }
        if (db.ChangeTracker.HasChanges()) await scope.ServiceProvider.GetRequiredService<ChatDbBudget>().SaveAsync(db, ct);
        this.lastHealthCheckpoint = now;
    }

    private async Task StopAsync(Session session)
    {
        var key = session.Configuration.ProviderId + ":" + session.Provider.ReceptionKey;
        if (this.receivers.Remove(key, out var receiver))
        {
            receiver.Cancellation.Cancel();
            try { await receiver.Task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            receiver.Cancellation.Dispose();
        }
        session.Cancellation.Cancel();
        await session.Provider.DisposeAsync(); session.Cancellation.Dispose(); this.sessions.Remove(session.Configuration.Id);
        runtime.Health[session.Configuration.Id] = ("stopped", "configuration-changed", null, null);
    }

    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync();
        try { foreach (var session in this.sessions.Values.ToArray()) await this.StopAsync(session); }
        finally { this.gate.Release(); }
    }

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
