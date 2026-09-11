// -----------------------------------------------------------------------
// <copyright file="ChatMigrationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed record ChatMigrationSelection(string ProductId, string SpawnId, string ContentMode = "metadata");
public sealed record ChatMigrationSource(bool Enabled, bool RemoteActions, string HostId, string GlobalChannel,
    IReadOnlyList<string> Channels, ChatSecretReference Credential, bool CredentialAvailable, int LongPollSeconds,
    IReadOnlyList<string> LegacyAllowedUsers, int LegacyMaxAttempts, TimeSpan LegacyNotificationTtl, TimeSpan LegacyTickInterval,
    bool UsesPublicTelegramEndpoint, bool IsTelegram);
public sealed record ChatMigrationScope(string ProductId, string SpawnId, string Title, string? ChannelId, string? TopicId, string? Reason);
public sealed record ChatMigrationIssue(string Kind, string SourceId, string Reason);
public sealed record ChatMigrationLink(string ChannelId, string ProviderUserId, string UserId, bool Ambiguous);
public sealed record ChatMigrationPreview(long Revision, string Owner, string Phase, string SourceHash, ChatMigrationSource Source,
    long LegacyCursor, long PluginCursor, IReadOnlyDictionary<string, int> Notifications, IReadOnlyList<ChatMigrationScope> Scopes,
    int ConfirmedLinks, int UnconfirmedSubscribers, IReadOnlyList<ChatMigrationIssue> Issues, IReadOnlyList<ChatMigrationLink> LinkTargets);
public sealed record ChatMigrationImport(long ExpectedRevision, string SourceHash, string BotId);
public sealed record ChatMigrationCutover(long ExpectedRevision, IReadOnlyList<ChatMigrationSelection> Activate);
public sealed record ChatMigrationRollback(long ExpectedRevision, bool PauseUnsupported = false);
public sealed record ChatRollbackPreview(long Revision, long LatestCursor, bool Lossless, IReadOnlyList<ChatMigrationIssue> Blockers,
    IReadOnlyList<string> UnsupportedSubscriptions);
internal sealed record ChatLegacyNotificationSnapshot(string Platform, string ScopeKind, string DedupeKey, string RouteState,
    string? RouteReason, string Status, string ProductId, string? SpawnId, string? WorkstreamId, string? ArtifactKey);

/// <summary>Explicit import and ownership changes. Preview and import perform no provider I/O.</summary>
public sealed partial class ChatMigrationService(ControlDbContext control, IProductDbContextFactory products,
    ForgeCapabilityAuthorizer authorizer, HostIdentityStore identities, ChatLegacySource legacy, ChatScopeResolver resolver,
    ChatEngineCoordinator engine, ChatConnectionSupervisor supervisor, ChatRuntimeState runtime, ChatContentPolicy policy,
    IOptions<ChatPluginOptions> options, TimeProvider clock, IServiceScopeFactory scopes)
{
    internal const string ConnectionId = "legacy-telegram";
    private static string Key(string kind, string source) => "legacy-" + kind + "-" + ChatEventCatalog.Hash(source)[..32].ToLowerInvariant();
    private static string ScopeKey(string product, string spawn) => ChatConfigurationScope.Spawn(product, spawn).Key;

    private ChatMigrationSource Source()
    {
        var telegram = legacy.Bridge.Telegram;
        var global = telegram.SupergroupChatId != 0 ? telegram.SupergroupChatId.ToString() : telegram.AllowedChatIds.FirstOrDefault().ToString();
        return new(legacy.Bridge.Enabled, legacy.Bridge.AllowRemoteActions, options.Value.HostId, global,
            telegram.AllowedChatIds.Where(c => c != 0).Select(c => c.ToString()).Append(global).Where(c => c != "0")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), legacy.Reference, legacy.CredentialAvailable,
            Math.Clamp(telegram.LongPollTimeoutSec, 1, 25), telegram.AllowedUserIds.Select(id => id.ToString()).ToArray(),
            legacy.Bridge.MaxAttempts, legacy.Bridge.NotificationTtl, legacy.Bridge.OutboundTickInterval,
            telegram.ApiBaseUrl.TrimEnd('/') == "https://api.telegram.org", legacy.Bridge.Platform == "telegram");
    }

    private string SourceHash(ChatMigrationSource source) => ChatEventCatalog.Hash(JsonSerializer.Serialize(source));

    public async Task<ChatMigrationPreview> PreviewAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await engine.InitializeAsync(ct);
        var state = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(ct);
        var source = this.Source();
        var topics = await control.ChatSpawnTopics.AsNoTracking().ToListAsync(ct);
        var notifications = await control.ChatNotifications.AsNoTracking().Where(n => n.Platform == "telegram" || n.Platform == ""
            || control.ChatMigrationItems.Any(m => m.Kind == "notification" && m.SourceId == n.Id)).ToListAsync(ct);
        var rows = new Dictionary<string, ChatMigrationScope>(StringComparer.Ordinal);
        var issues = new List<ChatMigrationIssue>();
        if (state is not null) issues.AddRange(JsonSerializer.Deserialize<ChatMigrationIssue[]>(state.IssuesJson) ?? []);
        if (!source.UsesPublicTelegramEndpoint || !source.IsTelegram) issues.Add(new("configuration", ConnectionId, "legacy-provider-settings-not-supported"));
        if (!source.CredentialAvailable) issues.Add(new("credential", ConnectionId, "legacy-credential-unavailable"));
        foreach (var topic in topics)
        {
            var owned = await resolver.ResolveAsync(new("open-questions", "preview", topic.ProductId, topic.SpawnId), ct);
            var reason = owned.Reason ?? (topic.ClosedAt is not null ? "topic-retired" : null);
            if (reason is null && owned.Spawn is not null && topic.CreatedAt < owned.Spawn.CreatedAt) reason = "topic-from-earlier-run";
            if (topics.Count(t => t.ChatId == topic.ChatId && t.ThreadId == topic.ThreadId) > 1) reason = "ambiguous-topic";
            var productId = owned.Product?.Id ?? topic.ProductId;
            rows[ScopeKey(productId, topic.SpawnId)] = new(productId, topic.SpawnId, owned.Spawn?.StoryTitle ?? topic.SpawnId,
                topic.ChatId, topic.ThreadId, reason);
            if (reason is not null) issues.Add(new("topic", topic.SpawnId, reason));
        }
        foreach (var notification in notifications)
        {
            var owned = await this.OwnedAsync(notification, ct);
            if (owned.Spawn is not null && owned.Product is not null)
                rows.TryAdd(ScopeKey(owned.Product.Id, owned.Spawn.Id), new(owned.Product.Id, owned.Spawn.Id, owned.Spawn.StoryTitle, notification.ChatId, null, owned.Reason));
            else if (!ChatEventCatalog.HostDefaults.Contains(notification.Kind)) issues.Add(new("notification", notification.Id, owned.Reason ?? "unresolved-scope"));
            if (owned.Reason is not null) issues.Add(new("notification", notification.Id, owned.Reason));
            if (notification.Status is "sending" or "uncertain") issues.Add(new("notification", notification.Id, "external-outcome-unknown"));
            if (string.IsNullOrWhiteSpace(notification.ChatId)) issues.Add(new("notification", notification.Id, "destination-missing"));
        }
        var links = identities.ActiveProviderLinks.Where(l => l.Provider == "telegram" && source.Channels.Contains(l.TenantId)).ToArray();
        foreach (var group in links.GroupBy(l => l.ProviderUserId).Where(g => g.Select(l => l.UserId).Distinct().Count() != 1))
            issues.Add(new("identity", group.Key, "ambiguous-legacy-identity"));
        var cursor = await control.ChatBridgeState.AsNoTracking().Select(s => s.LastUpdateId).SingleOrDefaultAsync(ct);
        var pluginCursor = await control.ChatConnectionStates.AsNoTracking().Where(s => s.ConnectionId == ConnectionId).Select(s => s.Cursor).SingleOrDefaultAsync(ct);
        var subscribers = await control.ChatSubscribers.AsNoTracking().Where(s => s.Platform == "telegram" || s.Platform == "").ToListAsync(ct);
        return new(state?.Revision ?? 0, state?.Owner ?? options.Value.Engine, state?.Phase ?? "not-imported", this.SourceHash(source), source,
            cursor, long.TryParse(pluginCursor, out var parsed) ? parsed : 0,
            notifications.GroupBy(n => n.Status).ToDictionary(g => g.Key, g => g.Count()), rows.Values.ToArray(), links.Length,
            subscribers.Count(s => !links.Any(l => l.ProviderUserId == s.ChatId && l.UserId == s.ForgeUserId)), issues.Distinct().ToArray(),
            links.Select(l => new ChatMigrationLink(l.TenantId, l.ProviderUserId, l.UserId, links.Where(other => other.ProviderUserId == l.ProviderUserId)
                .Select(other => other.UserId).Distinct().Count() > 1)).ToArray());
    }

    private async Task<ChatOwnedScope> OwnedAsync(ChatNotificationRow row, CancellationToken ct)
    {
        var owned = await resolver.ResolveAsync(new(row.Kind, row.DedupeKey, row.ProductId, row.SpawnId,
            row.WorkItemId, row.WorkstreamId, row.RepoId, ArtifactKey: row.ArtifactKey), ct);
        return owned.Spawn is not null && row.CreatedAt < owned.Spawn.CreatedAt ? owned with { Reason = "notification-from-earlier-run" } : owned;
    }

    private static void Revision(ChatMigrationStateRow? state, long expected)
    {
        if ((state?.Revision ?? 0) != expected) throw new ChatConfigurationException("revision-conflict", 409);
    }

    public async Task<ChatMigrationPreview> ImportAsync(ForgeActor? actor, ChatMigrationImport request, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await engine.InitializeAsync(ct);
        await engine.Commands.WaitAsync(ct);
        try
        {
            var source = this.Source();
            var state = await control.ChatMigrationState.SingleOrDefaultAsync(ct);
            Revision(state, request.ExpectedRevision);
            if (options.Value.Engine != "legacy" || options.Value.TelegramSlice.Enabled) throw new ChatConfigurationException("migration-requires-legacy-engine", 409);
            if (request.SourceHash != this.SourceHash(source)) throw new ChatConfigurationException("migration-preview-stale", 409);
            if (!source.UsesPublicTelegramEndpoint || !source.IsTelegram) throw new ChatConfigurationException("legacy-provider-settings-not-supported", 409);
            if (!long.TryParse(request.BotId, out var bot) || bot <= 0 || source.GlobalChannel == "0" || source.Channels.Count == 0)
                throw new ChatConfigurationException("legacy-bot-and-destination-required");
            var credential = await legacy.ReadAsync(source.Credential, ct);
            if (credential is null || !credential.StartsWith(request.BotId + ":", StringComparison.Ordinal))
                throw new ChatConfigurationException("legacy-credential-unavailable-or-mismatched", 409);
            if (state is not null && (state.SourceHash != request.SourceHash || state.Phase != "imported")) throw new ChatConfigurationException("migration-source-changed", 409);
            await engine.Routing.WaitAsync(ct);
            try
            {
                await using var tx = await control.Database.BeginTransactionAsync(ct);
                state ??= new() { SourceHash = request.SourceHash, SourceJson = JsonSerializer.Serialize(source), UpdatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds() };
                if (state.Revision == 0) control.ChatMigrationState.Add(state);
                await this.ImportCoreAsync(state, request.BotId, [], activate: false, ct);
                state.Revision++; state.UpdatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await control.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            finally { engine.Routing.Release(); }
            return await this.PreviewAsync(actor, ct);
        }
        finally { engine.Commands.Release(); }
    }
}