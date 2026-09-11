// -----------------------------------------------------------------------
// <copyright file="ChatManagementService.cs" company="Microsoft Corp.">
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

public sealed record ChatCredentialWrite(long ExpectedRevision, string Value);
public sealed record ChatPreviewRequest(string ContentMode, string? ProductId, string? Text);
public sealed record ChatPreviewView(string ContentMode, bool RedactedContentAllowed, string Text);
public sealed record ChatProductChoice(string Id, string Name);
public sealed record ChatSelfLink(string ProviderId, string ExternalTenantId, string ExternalUserId);
public sealed record ChatManagementView(bool CanManageConnections, bool CanManageHostAlerts, string? HostReason,
    bool InteractiveAvailable, bool HostRedactedContentAllowed, IReadOnlyList<ChatProductChoice> Products,
    IReadOnlyList<ChatSelfLink> Links, IReadOnlyList<ChatSecretStoreView> SecretStores);
public sealed record ChatSecretStoreView(string Id, bool Available, bool ExternallyManaged);
public sealed record ChatCredentialState(string Id, bool Configured, string? Store, bool ExternallyManaged, bool Available);
public sealed record ChatConnectionHealthView(string ConnectionId, long Revision, long AppliedRevision, string State, string? Reason,
    long? LastSendAt, long? LastReceiveAt, IReadOnlyList<ChatCredentialState> Credentials);
public sealed record ChatSummaryItem(string SpawnId, string Title, ChatRowSummary Summary);

public sealed class ChatManagementService(ControlDbContext control, IProductDbContextFactory products,
    ForgeCapabilityAuthorizer authorizer, HostIdentityStore identities, ChatConfigurationService configuration,
    IEnumerable<IChatSecretStore> stores, IChatProviderRegistry providers, ChatRuntimeState runtime,
    IOptions<ChatPluginOptions> options, ChatContentPolicy policy, ChatSecretResolver secrets)
{
    public async Task<ChatManagementView> ReadAsync(ForgeActor? actor, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.LinkSelf, new(), ct);
        var host = await authorizer.IsAllowedAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var readable = await authorizer.ReadableProductIdsAsync(actor, ct);
        var choices = await control.Products.AsNoTracking().Where(p => host || readable.Contains(p.Id))
            .Select(p => new ChatProductChoice(p.Id, p.Name)).ToListAsync(ct);
        return new(host, await authorizer.IsAllowedAsync(actor, ForgeCapabilities.HostAlertManage, new(), ct),
            options.Value.UnavailableReason ?? (!options.Value.PluginsEnabled ? options.Value.Engine == "legacy" ? "legacy-engine-active" : "host-chat-disabled" : null),
            ChatPluginInteractiveGate.AvailableFor(options.Value), policy.AllowsQuestions(null), choices,
            identities.ActiveProviderLinks.Where(l => l.UserId == actor!.UserId)
                .Select(l => new ChatSelfLink(l.Provider, l.TenantId, l.ProviderUserId)).ToArray(),
            host ? stores.Select(s => new ChatSecretStoreView(s.Id, s.Status.Available, s.Status.ExternallyManaged)).ToArray() : []);
    }

    public async Task<ChatConnectionHealthView> HealthAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        var connection = await configuration.GetConnectionAsync(actor, id, ct);
        var health = await control.ChatConnectionStates.AsNoTracking().SingleOrDefaultAsync(s => s.ConnectionId == id, ct);
        var descriptor = providers.Resolve(connection.ProviderId)?.Descriptor;
        var credentials = descriptor?.Credentials.Select(c =>
        {
            var reference = connection.SecretReferences.GetValueOrDefault(c.Id);
            var store = stores.SingleOrDefault(s => s.Id == reference?.Store);
            return new ChatCredentialState(c.Id, reference is not null, reference?.Store,
                store?.Status.ExternallyManaged ?? true, store?.Status.Available ?? false);
        }).ToArray() ?? [];
        var live = runtime.Health.GetValueOrDefault(id);
        return new(id, connection.Revision, health?.SettingsRevision ?? 0,
            !connection.Enabled ? "off" : live.Health ?? health?.Health ?? "unverified",
            live.Reason ?? health?.Reason, live.Sent ?? health?.LastSendAt, live.Received ?? health?.LastReceiveAt, credentials);
    }

    /// <summary>Write a fresh encrypted blob, then atomically point a revision at it. Never overwrite a shared credential.</summary>
    public async Task<ChatConnectionView> CredentialAsync(ForgeActor? actor, string id, string name, ChatCredentialWrite write, CancellationToken ct)
    {
        var connection = await configuration.GetConnectionAsync(actor, id, ct);
        if (connection.Revision != write.ExpectedRevision) throw new ChatConfigurationException("revision-conflict", 409);
        if (string.IsNullOrWhiteSpace(write.Value) || write.Value.Length > 8192 || write.Value.Any(char.IsControl)
            || providers.Resolve(connection.ProviderId)?.Descriptor.Credentials.Any(c => c.Id == name) != true)
            throw new ChatConfigurationException("invalid-credential");
        var store = stores.SingleOrDefault(s => s.Id == "dpapi" && s.Status.Available && !s.Status.ExternallyManaged)
            ?? throw new ChatConfigurationException("credential-store-unavailable", 409);
        var reference = await store.WriteAsync("chat-" + Guid.NewGuid().ToString("N"), write.Value, ct);
        var references = connection.SecretReferences.ToDictionary(p => p.Key, p => p.Value);
        references[name] = reference;
        try
        {
            return await configuration.PutConnectionAsync(actor, id, new(connection.Revision, connection.ProviderId, connection.InstallationId,
                connection.DisplayName, connection.Enabled, connection.SettingsVersion, connection.Settings, references, connection.ProductIds), ct);
        }
        catch
        {
            // Unreferenced new blobs may be removed; previous shared references remain intact.
            await store.DeleteAsync(reference, CancellationToken.None);
            throw;
        }
    }

    public async Task<ChatPreviewView> PreviewAsync(ForgeActor? actor, ChatPreviewRequest request, CancellationToken ct)
    {
        string? productId = null;
        if (request.ProductId is null) await authorizer.RequireAsync(actor, ForgeCapabilities.HostAlertManage, new(), ct);
        else
        {
            var product = await authorizer.ResolveProductAsync(request.ProductId, ct) ?? throw new ForgeAccessDeniedException();
            await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(product.Id), ct); productId = product.Id;
        }
        if (request.ContentMode is not ("metadata" or "redacted") || request.Text?.Length > 2000) throw new ChatConfigurationException("invalid-preview");
        var allow = policy.AllowsQuestions(productId, request.ContentMode);
        var credentials = new List<string>();
        if (allow)
        {
            foreach (var connection in await control.ChatConnections.AsNoTracking().Where(c => c.DeletedAt == null).ToListAsync(ct))
                credentials.AddRange(await secrets.ValuesAsync(connection, ct));
        }
        return new(request.ContentMode, allow, allow ? ChatContentPolicy.Redact(request.Text ?? "An operator response is needed.", credentials)
            : "Forge notification. Open Forge to review the current status.");
    }

    public async Task<IReadOnlyList<ChatSummaryItem>> SummariesAsync(ForgeActor? actor, string productId, CancellationToken ct)
    {
        var product = await authorizer.ResolveProductAsync(productId, ct) ?? throw new ForgeAccessDeniedException();
        await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(product.Id), ct);
        var db = products.CreateFor(product.Slug);
        var rows = await db.StorySpawns.AsNoTracking().Where(s => db.Repos.Any(r => r.Id == s.RepoId && r.ProductId == product.Id)).ToListAsync(ct);
        var summaries = await ChatSummaryService.ReadAsync(control, rows.Select(s => (s, product.Id)).ToList(), ct, options.Value);
        return rows.Select(s => new ChatSummaryItem(s.Id, s.StoryTitle, summaries[ChatConfigurationScope.Spawn(product.Id, s.Id).Key])).ToArray();
    }
}