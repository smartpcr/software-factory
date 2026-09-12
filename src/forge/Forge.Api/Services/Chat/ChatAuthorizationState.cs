// -----------------------------------------------------------------------
// <copyright file="ChatAuthorizationState.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using Common.Chat;
using Forge.Api.Auth;

public sealed class ChatAuthorizationOptions
{
    public SlackAuthorizationOptions Slack { get; set; } = new();
    public TelegramAuthorizationOptions Telegram { get; set; } = new();
}

public sealed class SlackAuthorizationOptions
{
    public string ClientId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public ChatSecretReference? ClientSecret { get; set; }
    public ChatSecretReference? AppToken { get; set; }
}

public sealed class TelegramAuthorizationOptions
{
    // A dedicated manager with Bot Management Mode enabled. Never a delivery bot.
    public ChatSecretReference? ManagerToken { get; set; }
}

public sealed record ChatAuthorizationAvailability(string ProviderId, bool Available, string? Reason, bool CanConfigure = false);
public sealed record ChatTelegramManagerSetup(string Token)
{
    public override string ToString() => "ChatTelegramManagerSetup { Token = [redacted] }";
}
public sealed record ChatTelegramManagerView(string Username);
public sealed record ChatSlackSetupApproval(string Code)
{
    public override string ToString() => "ChatSlackSetupApproval { Code = [redacted] }";
}
public sealed record ChatAuthorizationStart(string ProviderId, bool IncludePrivateChannels = false);
public sealed record ChatAuthorizationSave(string DisplayName, IReadOnlyList<string> ProductIds);
public sealed record ChatAuthorizationView(string Id, string ProviderId, string Status, DateTimeOffset ExpiresAt,
    string? AuthorizationUrl, string? AccountName, string? InstallationId, string? Reason, string? ConnectionId,
    string? SetupMode = null, string? ApprovalRequest = null, string? AppId = null);

/// <summary>Short-lived secrets stay on this host. Restart cancels unfinished authorization.</summary>
public sealed class ChatAuthorizationState : IDisposable
{
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal readonly Dictionary<string, Flow> Flows = new(StringComparer.Ordinal);
    internal long TelegramOffset;
    internal DateTimeOffset TelegramNextPoll;
    private readonly TimeProvider clock;
    private readonly ITimer cleanup;

    public ChatAuthorizationState(TimeProvider clock)
    {
        this.clock = clock;
        this.cleanup = clock.CreateTimer(_ =>
        {
            if (!this.Gate.Wait(0)) return;
            try { this.Prune(); }
            finally { this.Gate.Release(); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal void Prune()
    {
        foreach (var id in this.Flows.Where(p => p.Value.ExpiresAt <= clock.GetUtcNow()).Select(p => p.Key).ToArray())
        {
            this.Flows[id].Clear();
            this.Flows.Remove(id);
        }
    }

    internal Flow Create(ForgeActor actor, ChatAuthorizationStart request)
    {
        this.Prune();
        if (this.Flows.Values.Count(f => f.UserId == actor.UserId && f.Status is "waiting" or "approved") >= 4
            || this.Flows.Count >= 64) throw new ChatConfigurationException("authorization-limit", 429);
        var flow = new Flow
        {
            Id = Nonce(), State = Nonce(), UserId = actor.UserId, Revision = actor.Revision, ProviderId = request.ProviderId,
            ExpiresAt = clock.GetUtcNow().AddMinutes(10), IncludePrivateChannels = request.IncludePrivateChannels,
        };
        this.Flows.Add(flow.Id, flow);
        return flow;
    }

    internal Flow Owned(ForgeActor actor, string id)
    {
        this.Prune();
        if (!this.Flows.TryGetValue(id, out var flow) || flow.UserId != actor.UserId || flow.Revision != actor.Revision)
            throw new ChatConfigurationException("authorization-expired", 404);
        return flow;
    }

    internal static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public void Dispose()
    {
        this.cleanup.Dispose();
        this.Gate.Wait();
        try { foreach (var flow in this.Flows.Values) flow.Clear(); }
        finally { this.Gate.Release(); }
    }

    internal sealed class Flow
    {
        public required string Id, State, UserId, Revision, ProviderId;
        public required DateTimeOffset ExpiresAt;
        public string Status = "waiting";
        public bool IncludePrivateChannels;
        public string? AuthorizationUrl, Code, BotToken, AccountName, InstallationId, Reason, ConnectionId, BotUsername;
        public string? SetupMode, SetupTicket, AppToken, AppId;
        public ChatAuthorizationView View() => new(this.Id, this.ProviderId, this.Status, this.ExpiresAt,
            this.Status == "waiting" ? this.AuthorizationUrl : null, this.AccountName, this.InstallationId, this.Reason, this.ConnectionId,
            this.SetupMode, this.Status == "waiting" && this.SetupTicket is not null ? "/slackauthticket " + this.SetupTicket : null, this.AppId);
        public void Clear() { this.Code = null; this.BotToken = null; this.AuthorizationUrl = null; this.SetupTicket = null; this.AppToken = null; }
        public void Fail(string reason) { this.Status = "failed"; this.Reason = reason; this.Clear(); }
    }
}