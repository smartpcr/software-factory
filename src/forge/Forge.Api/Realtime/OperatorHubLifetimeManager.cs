namespace Forge.Api.Realtime;

using System.Collections.Concurrent;
using System.Security.Claims;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Legacy ForgeHub broadcasts combine products and host events. Until those
/// payloads carry individual product targets, a subscriber needs host admin
/// authority and explicit membership in every product. Check each invocation
/// and delivery, including after cookie expiry or grant/enrollment revocation.
/// </summary>
public sealed class OperatorHubAccess(IServiceScopeFactory scopes)
{
    public async Task<bool> IsAllowedAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (principal is null) return false;
        using var scope = scopes.CreateScope();
        var actor = await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromBrowserAsync(principal, ct);
        var authorizer = scope.ServiceProvider.GetRequiredService<ForgeCapabilityAuthorizer>();
        if (!await authorizer.IsAllowedAsync(actor, ForgeCapabilities.IdentityManage, new(), ct)) return false;
        var readable = await authorizer.ReadableProductIdsAsync(actor, ct);
        return readable.Count == await scope.ServiceProvider.GetRequiredService<ControlDbContext>().Products.CountAsync(ct);
    }
}

public sealed class OperatorHubLifetimeManager(ILogger<DefaultHubLifetimeManager<ForgeHub>> logger, OperatorHubAccess access)
    : DefaultHubLifetimeManager<ForgeHub>(logger)
{
    private readonly ConcurrentDictionary<string, HubConnectionContext> connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> groups = new();

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        if (!await access.IsAllowedAsync(connection.User)) { connection.Abort(); throw new HubException("This host-wide realtime feed requires a host administrator with access to every product."); }
        this.connections[connection.ConnectionId] = connection;
        await base.OnConnectedAsync(connection);
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        this.connections.TryRemove(connection.ConnectionId, out _);
        foreach (var group in this.groups.Values) group.TryRemove(connection.ConnectionId, out _);
        await base.OnDisconnectedAsync(connection);
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        if (await this.AllowedAsync(connectionId, cancellationToken))
        {
            this.groups.GetOrAdd(groupName, _ => new())[connectionId] = 0;
            await base.AddToGroupAsync(connectionId, groupName, cancellationToken);
        }
    }

    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        if (this.groups.TryGetValue(groupName, out var group)) group.TryRemove(connectionId, out _);
        return base.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
    }

    private async Task<bool> AllowedAsync(string id, CancellationToken ct)
    {
        if (!this.connections.TryGetValue(id, out var connection)) return false;
        if (await access.IsAllowedAsync(connection.User, ct)) return true;
        connection.Abort();
        return false;
    }

    private async Task DeliverAsync(IEnumerable<string> ids, string method, object?[] args, CancellationToken ct)
    {
        foreach (var id in ids.Distinct().ToArray())
            if (await this.AllowedAsync(id, ct)) await base.SendConnectionAsync(id, method, args, ct);
    }

    private IEnumerable<string> GroupMembers(string group) => this.groups.TryGetValue(group, out var members) ? members.Keys : Array.Empty<string>();
    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(this.connections.Keys, methodName, args, cancellationToken);
    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => this.DeliverAsync(this.connections.Keys.Except(excludedConnectionIds), methodName, args, cancellationToken);
    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync([connectionId], methodName, args, cancellationToken);
    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(connectionIds, methodName, args, cancellationToken);
    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(this.GroupMembers(groupName), methodName, args, cancellationToken);
    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(groupNames.SelectMany(this.GroupMembers), methodName, args, cancellationToken);
    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) => this.DeliverAsync(this.GroupMembers(groupName).Except(excludedConnectionIds), methodName, args, cancellationToken);
    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(this.connections.Values.Where(c => c.UserIdentifier == userId).Select(c => c.ConnectionId), methodName, args, cancellationToken);
    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) => this.DeliverAsync(this.connections.Values.Where(c => c.UserIdentifier is not null && userIds.Contains(c.UserIdentifier)).Select(c => c.ConnectionId), methodName, args, cancellationToken);
}
