// -----------------------------------------------------------------------
// <copyright file="OperatorSessions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;

/// <summary>Process-local session registry: every host restart invalidates all browser cookies.</summary>
public sealed class OperatorSessions(HostIdentityStore identities, TimeProvider clock)
{
    public const string Scheme = "ForgeOperator";
    public const string SessionClaim = "forge.session";
    private readonly ConcurrentDictionary<string, Session> sessions = new();

    public ClaimsPrincipal Issue(OperatorEnrollment enrollment)
    {
        var now = clock.GetUtcNow();
        foreach (var entry in this.sessions.Where(x => x.Value.ExpiresAt <= now)) this.sessions.TryRemove(entry.Key, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        this.sessions[id] = new(enrollment.UserId, enrollment.Revision, now.AddHours(8));
        return new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, enrollment.UserId), new Claim(ClaimTypes.Name, enrollment.UserId),
            new Claim(SessionClaim, id),
        }, Scheme));
    }

    public bool IsCurrent(string? id, string? userId)
    {
        if (id is null || userId is null || !this.sessions.TryGetValue(id, out var session)
            || session.UserId != userId || session.ExpiresAt <= clock.GetUtcNow()) return false;
        var enrollment = identities.GetEnrollment(userId);
        return enrollment is { Enabled: true } && enrollment.Revision == session.Revision;
    }

    public bool IsCurrent(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true
        && this.IsCurrent(principal.FindFirstValue(SessionClaim), principal.FindFirstValue(ClaimTypes.NameIdentifier));

    public void Revoke(string? id) { if (id is not null) this.sessions.TryRemove(id, out _); }
    public void RevokeUser(string userId)
    {
        foreach (var entry in this.sessions.Where(x => x.Value.UserId == userId)) this.sessions.TryRemove(entry.Key, out _);
    }

    private sealed record Session(string UserId, string Revision, DateTimeOffset ExpiresAt);
}
