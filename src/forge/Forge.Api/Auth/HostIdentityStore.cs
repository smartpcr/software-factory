// -----------------------------------------------------------------------
// <copyright file="HostIdentityStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record OperatorEnrollment(string UserId, bool HostAdmin, bool Enabled, string Revision);
public sealed record OperatorLink(string Id, string Provider, string TenantId, string ProviderUserId, string UserId, string Revision);
public sealed record OperatorChallenge(string Hash, string UserId, string Revision, DateTimeOffset ExpiresAt,
    string Purpose, string? Provider = null, string? TenantId = null, string? ProviderUserId = null);

/// <summary>
/// Host-user authority, protected by Windows DPAPI. HTTP cannot enroll users or
/// issue browser challenges. A named mutex serializes CLI/host read-modify-write.
/// Missing state is empty; corrupt/inaccessible state fails closed.
/// </summary>
public sealed class HostIdentityStore
{
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly string mutexName;

    public HostIdentityStore(string path, TimeProvider? clock = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Identity store path must be absolute.", nameof(path));
        this.path = Path.GetFullPath(path);
        this.clock = clock ?? TimeProvider.System;
        this.mutexName = @"Local\ForgeIdentity-" + Hash(this.path.ToUpperInvariant());
    }

    public static string DefaultPath() => Environment.GetEnvironmentVariable("FORGE_IDENTITY_STORE_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge", "operator-identities.bin");

    public OperatorEnrollment? GetEnrollment(string userId) => this.Read(s => s.Enrollments.GetValueOrDefault(userId));
    public IReadOnlyList<string> EnrolledUserIds => this.Read(s => s.Enrollments.Values.Where(e => e.Enabled).Select(e => e.UserId).ToArray());
    public IReadOnlyList<OperatorLink> ActiveProviderLinks => this.Read(s => s.Links.Where(l => s.Enrollments.TryGetValue(l.UserId, out var e)
        && e.Enabled && e.Revision == l.Revision).ToArray());
    public void PurgeExpiredChallenges()
    {
        var cutoff = this.clock.GetUtcNow().AddDays(-1);
        if (!this.Read(s => s.Challenges.Any(c => c.ExpiresAt <= cutoff))) return;
        this.Write(s => s.Challenges.RemoveAll(c => c.ExpiresAt <= cutoff));
    }
    public string? ChallengeUserId(string token, string purpose) => this.Read(s => this.FindChallenge(s, token, purpose)?.UserId);
    public bool HasEnrollment => this.Read(s => s.Enrollments.Values.Any(e => e.Enabled));

    // Only the host-local command calls this; it is deliberately absent from HTTP routes.
    public void Enroll(string userId, bool hostAdmin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        this.Write(s =>
        {
            s.Enrollments[userId] = new(userId, hostAdmin, true, Guid.NewGuid().ToString("N"));
            s.Challenges.RemoveAll(c => c.UserId == userId);
            s.Links.RemoveAll(l => l.UserId == userId);
            return true;
        });
    }

    public void RevokeEnrollment(string userId) => this.Write(s =>
    {
        if (s.Enrollments.TryGetValue(userId, out var enrolled))
            s.Enrollments[userId] = enrolled with { Enabled = false, Revision = Guid.NewGuid().ToString("N") };
        s.Challenges.RemoveAll(c => c.UserId == userId);
        s.Links.RemoveAll(l => l.UserId == userId);
        return true;
    });

    public string IssueBrowserChallenge(string userId) => this.IssueChallenge(userId, "browser");

    public string IssueLinkChallenge(string userId, string provider, string tenantId, string providerUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);
        return this.IssueChallenge(userId, "link", provider, tenantId, providerUserId);
    }

    public OperatorEnrollment? RedeemBrowserChallenge(string token) => this.Write(s =>
    {
        var challenge = this.FindChallenge(s, token, "browser");
        if (challenge is null) return null;
        s.Challenges.Remove(challenge);
        return s.Enrollments[challenge.UserId];
    });

    public OperatorLink? RedeemLinkChallenge(string token, string provider, string tenantId, string providerUserId) => this.Write(s =>
    {
        var challenge = this.FindChallenge(s, token, "link");
        if (challenge is null || challenge.Provider != provider || challenge.TenantId != tenantId || challenge.ProviderUserId != providerUserId)
            return null;
        s.Challenges.Remove(challenge);
        s.Links.RemoveAll(l => l.Provider == provider && l.TenantId == tenantId && l.ProviderUserId == providerUserId);
        var link = new OperatorLink(Guid.NewGuid().ToString("N"), provider, tenantId, providerUserId,
            challenge.UserId, challenge.Revision);
        s.Links.Add(link);
        return link;
    });

    public OperatorLink? FindLink(string provider, string tenantId, string providerUserId) => this.Read(s =>
        s.Links.SingleOrDefault(l => l.Provider == provider && l.TenantId == tenantId && l.ProviderUserId == providerUserId
            && s.Enrollments.TryGetValue(l.UserId, out var e) && e.Enabled && e.Revision == l.Revision));

    public bool IsCurrentLink(string id, string userId, string revision) => this.Read(s =>
        s.Links.Any(l => l.Id == id && l.UserId == userId && l.Revision == revision));

    public void RevokeLinks(string userId) => this.Write(s =>
    {
        s.Links.RemoveAll(l => l.UserId == userId);
        s.Challenges.RemoveAll(c => c.UserId == userId && c.Purpose == "link");
        return true;
    });

    private string IssueChallenge(string userId, string purpose, string? provider = null, string? tenantId = null, string? providerUserId = null) => this.Write(s =>
    {
        if (!s.Enrollments.TryGetValue(userId, out var e) || !e.Enabled)
            throw new InvalidOperationException("Needs operator enrollment.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        s.Challenges.RemoveAll(c => c.ExpiresAt <= this.clock.GetUtcNow() || (c.UserId == userId && c.Purpose == purpose));
        s.Challenges.Add(new(Hash(token), userId, e.Revision, this.clock.GetUtcNow().AddMinutes(2), purpose, provider, tenantId, providerUserId));
        return token;
    });

    private OperatorChallenge? FindChallenge(IdentityState state, string token, string purpose)
    {
        if (token.Length != 64) return null;
        var hash = Hash(token);
        return state.Challenges.SingleOrDefault(c => c.Hash == hash && c.Purpose == purpose && c.ExpiresAt > this.clock.GetUtcNow()
            && state.Enrollments.TryGetValue(c.UserId, out var e) && e.Enabled && e.Revision == c.Revision);
    }

    private T Read<T>(Func<IdentityState, T> read) => this.Access(read, write: false);
    private T Write<T>(Func<IdentityState, T> write) => this.Access(write, write: true);

    private T Access<T>(Func<IdentityState, T> action, bool write)
    {
        using var mutex = new Mutex(false, this.mutexName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Identity store is busy."); }
        catch (AbandonedMutexException) { /* Ownership acquired; atomic file replacement preserves state. */ }
        try
        {
            var state = File.Exists(this.path)
                ? JsonSerializer.Deserialize<IdentityState>(ProtectedData.Unprotect(File.ReadAllBytes(this.path), null, DataProtectionScope.CurrentUser))
                    ?? throw new InvalidDataException("Invalid identity store.")
                : new IdentityState();
            var result = action(state);
            if (write)
            {
                var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state), null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
                var temp = this.path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temp, encrypted); File.Move(temp, this.path, overwrite: true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            return result;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public sealed class IdentityState
    {
        public Dictionary<string, OperatorEnrollment> Enrollments { get; set; } = new(StringComparer.Ordinal);
        public List<OperatorChallenge> Challenges { get; set; } = new();
        public List<OperatorLink> Links { get; set; } = new();
    }
}
