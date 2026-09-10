namespace Forge.Api.Tests.Auth;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Api.Auth;
using Xunit;

public sealed class TestOperatorClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => this.Now;
}

public sealed class HostIdentityTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "forge-identity-" + Guid.NewGuid().ToString("N") + ".bin");
    private readonly TestOperatorClock clock = new();
    private HostIdentityStore Store => new(this.path, this.clock);
    public void Dispose() { if (File.Exists(this.path)) File.Delete(this.path); }

    [Fact]
    public void Enrollment_Is_Explicit_Encrypted_And_Only_Hashed_Challenges_Are_Persisted()
    {
        var store = this.Store;
        Assert.False(store.HasEnrollment);
        Assert.Throws<InvalidOperationException>(() => store.IssueBrowserChallenge("operator"));
        store.Enroll("operator", true);
        var token = store.IssueBrowserChallenge("operator");
        Assert.Equal(64, token.Length);
        var bytes = File.ReadAllBytes(this.path);
        Assert.DoesNotContain("operator", Encoding.UTF8.GetString(bytes));
        var clear = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(clear));
        var state = JsonSerializer.Deserialize<HostIdentityStore.IdentityState>(clear)!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), Assert.Single(state.Challenges).Hash);
        Assert.Equal("operator", store.RedeemBrowserChallenge(token)!.UserId);
        Assert.Null(store.RedeemBrowserChallenge(token));
    }

    [Fact]
    public async Task Pairing_Is_Consumed_Once_Across_Concurrent_Store_Instances()
    {
        this.Store.Enroll("u", true);
        var token = this.Store.IssueBrowserChallenge("u");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => this.Store.RedeemBrowserChallenge(token))));
        Assert.Single(results.Where(r => r is not null));
    }

    [Fact]
    public void Challenges_Expire_At_Two_Minutes_And_Cannot_Cross_Purposes_Or_Senders()
    {
        var store = this.Store;
        store.Enroll("u", false);
        var browser = store.IssueBrowserChallenge("u");
        Assert.Null(store.RedeemLinkChallenge(browser, "telegram", "group", "sender"));
        this.clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(store.RedeemBrowserChallenge(browser));
        var link = store.IssueLinkChallenge("u", "telegram", "group", "sender");
        Assert.Null(store.RedeemLinkChallenge(link, "slack", "group", "sender"));
        Assert.Null(store.RedeemLinkChallenge(link, "telegram", "other", "sender"));
        Assert.Null(store.RedeemLinkChallenge(link, "telegram", "group", "intruder"));
        Assert.Null(store.RedeemBrowserChallenge(link));
        Assert.NotNull(store.RedeemLinkChallenge(link, "telegram", "group", "sender"));
        Assert.Null(store.RedeemLinkChallenge(link, "telegram", "group", "sender"));
        store.RevokeLinks("u");
        Assert.Null(store.FindLink("telegram", "group", "sender"));
    }

    [Fact]
    public void Sessions_Expire_At_Eight_Hours_And_Revocation_And_Restart_Invalidate_Them()
    {
        var store = this.Store;
        store.Enroll("u", true);
        var sessions = new OperatorSessions(store, this.clock);
        var first = sessions.Issue(store.GetEnrollment("u")!);
        Assert.True(sessions.IsCurrent(first));
        Assert.False(new OperatorSessions(store, this.clock).IsCurrent(first));
        this.clock.Now += TimeSpan.FromHours(8);
        Assert.False(sessions.IsCurrent(first));
        var second = sessions.Issue(store.GetEnrollment("u")!);
        sessions.RevokeUser("u");
        Assert.False(sessions.IsCurrent(second));
        var third = sessions.Issue(store.GetEnrollment("u")!);
        store.Enroll("u", true);
        Assert.False(sessions.IsCurrent(third));
        var fourth = sessions.Issue(store.GetEnrollment("u")!);
        store.RevokeEnrollment("u");
        Assert.False(sessions.IsCurrent(fourth));
    }
}
