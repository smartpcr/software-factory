// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Configuration only. No domain execution, provider send, or receiver is started here.</summary>
public sealed partial class ChatConfigurationService(
    ControlDbContext control, IProductDbContextFactory products, ForgeCapabilityAuthorizer authorizer,
    IChatProviderRegistry providers, WorkstreamPluginRegistry plugins, IOptions<ChatPluginOptions> options, ChatTelegramSliceService slice)
{
    private static readonly string[] SpawnKinds = ["open-questions", "workstream-stalled"];
    private static readonly string[] HostKinds = ["queue-strain", "human-interaction"];

    public async Task<IReadOnlyList<ChatConnectionView>> ListConnectionsAsync(ForgeActor? actor, string? productId, CancellationToken ct = default)
    {
        var query = control.ChatConnections.AsNoTracking().Where(c => c.DeletedAt == null);
        if (productId is null) await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        else
        {
            var product = await this.ProductAsync(actor, productId, null, false, ct);
            query = query.Where(c => control.ChatConnectionProducts.Any(g => g.ConnectionId == c.Id && g.ProductId == product.Id));
        }
        return (await query.ToListAsync(ct)).Select(this.ConnectionView).ToArray();
    }

    public async Task<ChatConnectionDetails> GetConnectionAsync(ForgeActor? actor, string id, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var row = await this.ConnectionAsync(id, ct);
        var grants = await control.ChatConnectionProducts.AsNoTracking().Where(g => g.ConnectionId == id).Select(g => g.ProductId).ToListAsync(ct);
        using var settings = JsonDocument.Parse(row.SettingsJson);
        return new(row.Id, row.ProviderId, row.InstallationId, row.DisplayName, row.Enabled, row.Revision, row.SettingsVersion,
            settings.RootElement.Clone(), JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(row.SecretReferencesJson)!, grants);
    }

    public async Task<ChatConnectionView> PutConnectionAsync(ForgeActor? actor, string id, ChatConnectionWrite write, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        RequireText(id); RequireText(write.ProviderId); RequireText(write.DisplayName); RequireText(write.InstallationId);
        var row = await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == id, ct);
        CheckRevision(row?.Revision, write.ExpectedRevision);
        if (row?.DeletedAt is not null) throw new ChatConfigurationException("connection-retired", 409);
        var provider = providers.Resolve(write.ProviderId);
        if (provider is null && (row is null || row.ProviderId != write.ProviderId)) throw new ChatConfigurationException("provider-unavailable", 409);
        ValidateJson(write.Settings, write.SettingsVersion);
        if (provider is not null && provider.Descriptor.SettingsVersions.Contains(write.SettingsVersion)
            && !provider.ValidateSettings(write.SettingsVersion, write.Settings).Valid) throw new ChatConfigurationException("invalid-provider-settings");
        if (row is not null && (row.ProviderId != write.ProviderId || row.InstallationId != write.InstallationId))
            throw new ChatConfigurationException("connection-identity-immutable", 409);
        if (write.SecretReferences is null || write.ProductIds is null) throw new ChatConfigurationException("invalid-connection-settings");
        foreach (var (name, reference) in write.SecretReferences)
        {
            if (reference is null || provider is not null && !provider.Descriptor.Credentials.Any(c => c.Id == name))
                throw new ChatConfigurationException("invalid-credential-reference");
            RequireReference(reference!.Store); RequireReference(reference.Key);
        }
        var grantedIds = write.ProductIds.Distinct(StringComparer.Ordinal).ToList();
        if (grantedIds.Count != write.ProductIds.Count || grantedIds.Count > 256
            || await control.Products.CountAsync(p => grantedIds.Contains(p.Id), ct) != grantedIds.Count)
            throw new ChatConfigurationException("invalid-product-grants");

        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        row ??= new ChatConnectionRow { Id = id, Revision = 0 };
        if (row.Revision == 0) control.ChatConnections.Add(row);
        row.ProviderId = write.ProviderId; row.InstallationId = write.InstallationId;
        row.DisplayName = write.DisplayName; row.Enabled = write.Enabled;
        row.SettingsVersion = write.SettingsVersion; row.SettingsJson = write.Settings.GetRawText();
        row.SecretReferencesJson = JsonSerializer.Serialize(write.SecretReferences); row.Revision++;
        var grants = await control.ChatConnectionProducts.Where(g => g.ConnectionId == id).ToListAsync(ct);
        control.ChatConnectionProducts.RemoveRange(grants.Where(g => !grantedIds.Contains(g.ProductId, StringComparer.Ordinal)));
        foreach (var productId in grantedIds.Where(p => !grants.Any(g => g.ProductId == p)))
            control.ChatConnectionProducts.Add(new ChatConnectionProductRow { ConnectionId = id, ProductId = productId });
        // Validation belongs to the connection revision. A settings/credential change invalidates it.
        foreach (var destination in await control.ChatDestinations.Where(d => d.ConnectionId == id && d.DeletedAt == null).ToListAsync(ct))
        {
            destination.AccessState = "unverified"; destination.ValidatedAt = null; destination.Revision++;
        }
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return this.ConnectionView(row);
    }

    public async Task DeleteConnectionAsync(ForgeActor? actor, string id, long revision, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var row = await this.ConnectionAsync(id, ct);
        CheckRevision(row.Revision, revision);
        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        row.Enabled = false; row.DeletedAt = DateTimeOffset.UtcNow; row.Revision++;
        var destinations = await control.ChatDestinations.Where(d => d.ConnectionId == id).Select(d => d.Id).ToListAsync(ct);
        await this.RetireAsync(await control.ChatSubscriptions.Where(s => destinations.Contains(s.DestinationId) && s.RetiredAt == null).ToListAsync(ct), ct);
        foreach (var destination in await control.ChatDestinations.Where(d => d.ConnectionId == id && d.DeletedAt == null).ToListAsync(ct))
        { destination.DeletedAt = row.DeletedAt; destination.Revision++; }
        control.ChatConnectionProducts.RemoveRange(await control.ChatConnectionProducts.Where(g => g.ConnectionId == id).ToListAsync(ct));
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ChatDestinationRow>> ListDestinationsAsync(ForgeActor? actor, string connectionId, string? productId, CancellationToken ct = default)
    {
        if (productId is null) await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        else
        {
            var product = await this.ProductAsync(actor, productId, null, false, ct);
            await this.RequireConnectionGrantAsync(connectionId, product.Id, ct);
        }
        await this.ConnectionAsync(connectionId, ct);
        return await control.ChatDestinations.AsNoTracking().Where(d => d.ConnectionId == connectionId && d.DeletedAt == null).ToListAsync(ct);
    }

    public async Task<ChatDestinationRow> PutDestinationAsync(ForgeActor? actor, string connectionId, string id, ChatDestinationWrite write, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await this.ConnectionAsync(connectionId, ct);
        RequireText(id); RequireText(write.ExternalTenantId); RequireText(write.ChannelId); RequireText(write.DisplayName); RequireText(write.Kind);
        var row = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == id, ct);
        CheckRevision(row?.Revision, write.ExpectedRevision);
        if (row is not null && (row.DeletedAt is not null || row.ConnectionId != connectionId
            || row.ExternalTenantId != write.ExternalTenantId || row.ChannelId != write.ChannelId))
            throw new ChatConfigurationException("destination-identity-immutable", 409);
        row ??= new ChatDestinationRow { Id = id, ConnectionId = connectionId, ExternalTenantId = write.ExternalTenantId, ChannelId = write.ChannelId, Revision = 0 };
        if (row.Revision == 0) control.ChatDestinations.Add(row);
        row.DisplayName = write.DisplayName; row.Kind = write.Kind; row.Revision++;
        row.AccessState = "unverified"; row.ValidatedAt = null;
        await this.SaveAsync(ct);
        return row;
    }

    public async Task DeleteDestinationAsync(ForgeActor? actor, string connectionId, string id, long revision, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var row = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == id && d.ConnectionId == connectionId && d.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("destination-not-found", 404);
        CheckRevision(row.Revision, revision);
        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        row.DeletedAt = DateTimeOffset.UtcNow; row.Revision++;
        await this.RetireAsync(await control.ChatSubscriptions.Where(s => s.DestinationId == id && s.RetiredAt == null).ToListAsync(ct), ct);
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private ChatConnectionView ConnectionView(ChatConnectionRow row)
    {
        var reason = this.ConnectionReason(row);
        return new(row.Id, row.ProviderId, row.DisplayName, row.Enabled, row.Revision, row.SettingsVersion,
            !row.Enabled ? "off" : reason is not null ? "paused" : "configured", reason);
    }

    private string? ConnectionReason(ChatConnectionRow row)
    {
        var provider = providers.Resolve(row.ProviderId);
        if (provider is null) return "provider-unavailable";
        if (!provider.Descriptor.SettingsVersions.Contains(row.SettingsVersion)) return "unsupported-connection-settings-version";
        try
        {
            using var settings = JsonDocument.Parse(row.SettingsJson);
            if (!provider.ValidateSettings(row.SettingsVersion, settings.RootElement).Valid) return "invalid-provider-settings";
            var refs = JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(row.SecretReferencesJson);
            if (refs is null || provider.Descriptor.Credentials.Any(c => c.Required && !refs.ContainsKey(c.Id))) return "credentials-required";
        }
        catch (JsonException) { return "invalid-connection-settings"; }
        return null;
    }

    private async Task<ProductRow> ProductAsync(ForgeActor? actor, string productId, string? spawnId, bool write, CancellationToken ct)
    {
        var product = await authorizer.ResolveProductAsync(productId, ct) ?? throw new ForgeAccessDeniedException();
        await authorizer.RequireAsync(actor, write ? ForgeCapabilities.SubscriptionManage : ForgeCapabilities.SpawnRead, new(product.Id, spawnId), ct);
        return product;
    }

    private async Task RequireConnectionGrantAsync(string connectionId, string productId, CancellationToken ct)
    {
        if (!await control.ChatConnectionProducts.AnyAsync(g => g.ConnectionId == connectionId && g.ProductId == productId, ct))
            throw new ForgeAccessDeniedException();
    }

    private async Task<ChatConnectionRow> ConnectionAsync(string id, CancellationToken ct)
        => await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
            ?? throw new ChatConfigurationException("connection-not-found", 404);

    internal static string RunIdentity(StorySpawnRow spawn) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { spawn.StoryId, spawn.RepoId, Created = spawn.CreatedAt.UtcTicks }))));
    private static void CheckRevision(long? actual, long expected)
    {
        if (expected < 0 || (actual ?? 0) != expected) throw new ChatConfigurationException("revision-conflict", 409);
    }
    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new ChatConfigurationException("invalid-text");
    }
    private static void RequireReference(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) || value.Contains("..", StringComparison.Ordinal))
            throw new ChatConfigurationException("invalid-credential-reference");
    }
    private static void ValidateJson(JsonElement settings, int version)
    {
        if (version < 1 || settings.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(settings.GetRawText()) > 16384)
            throw new ChatConfigurationException("invalid-settings");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await ChatScopeLifecycleService.ConfigurationChangedAsync(control, ct);
        try { await control.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ChatConfigurationException("revision-conflict", 409); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        { throw new ChatConfigurationException("configuration-conflict", 409); }
    }

    private async Task RetireAsync(IReadOnlyList<ChatSubscriptionRow> subscriptions, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in subscriptions) { row.RetiredAt = now; row.Paused = true; row.Revision++; }
        var ids = subscriptions.Select(s => s.Id).ToList();
        foreach (var binding in await control.ChatConversationBindings.Where(b => ids.Contains(b.SubscriptionId) && b.RetiredAt == null).ToListAsync(ct))
        { binding.RetiredAt = now; binding.Revision++; }
    }
}