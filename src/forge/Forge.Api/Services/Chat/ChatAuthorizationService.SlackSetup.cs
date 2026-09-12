// -----------------------------------------------------------------------
// <copyright file="ChatAuthorizationService.SlackSetup.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Microsoft.EntityFrameworkCore;
using static ChatAuthorizationProvider;

public sealed partial class ChatAuthorizationService
{
    private bool CanConfigureSlack => string.IsNullOrWhiteSpace(this.Options.Slack.ClientId)
        && string.IsNullOrWhiteSpace(this.Options.Slack.AppId) && string.IsNullOrWhiteSpace(this.Options.Slack.RedirectUri)
        && this.Options.Slack.ClientSecret is null && this.Options.Slack.AppToken is null;

    public async Task<ChatAuthorizationView> BeginSlackSetupAsync(ForgeActor? actor, ChatAuthorizationStart request, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        if (request.ProviderId != "slack") throw new ChatConfigurationException("provider-unavailable", 400);
        if (!this.CanConfigureSlack) throw new ChatConfigurationException("authorization-slack-externally-configured", 409);
        _ = this.WritableStore();
        await state.Gate.WaitAsync(ct);
        try
        {
            var flow = state.Create(actor!, request);
            flow.SetupMode = "slack-cli";
            try
            {
                var result = await provider.SlackSetupAsync("apps.hosted.generateAuthTicket", null,
                    new { slack_cli_version = "v4.7" }, ct);
                var ticket = String(result, "ticket");
                if (ticket is null || ticket.Length is < 16 or > 1024 || !ticket.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_'))
                    throw new ChatConfigurationException("authorization-provider-rejected");
                await this.RequireCurrentSetupActorAsync(actor, flow, ct);
                flow.SetupTicket = ticket;
                flow.AuthorizationUrl = "https://app.slack.com/client/";
                return flow.View();
            }
            catch { flow.Fail("authorization-provider-unavailable"); throw; }
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ChatAuthorizationView> ApproveSlackSetupAsync(ForgeActor? actor, string id, ChatSlackSetupApproval request, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var code = request.Code?.Trim();
        if (code is null || code.Length is < 4 or > 256 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ChatConfigurationException("authorization-slack-code-required", 400);
        await state.Gate.WaitAsync(ct);
        try
        {
            var flow = state.Owned(actor!, id);
            if (flow.ProviderId != "slack" || flow.SetupMode != "slack-cli")
                throw new ChatConfigurationException("authorization-not-approved", 409);
            if (flow.Status is "approved" or "saved") return flow.View();
            if (flow.Status != "waiting" || flow.SetupTicket is null) throw new ChatConfigurationException("authorization-expired", 409);
            var ticket = flow.SetupTicket;
            flow.SetupTicket = null; flow.AuthorizationUrl = null; flow.Status = "exchanging";
            try
            {
                // Consume this proof only once; uncertain exchanges/creation are never automatically replayed.
                var account = await provider.SlackSetupAsync("apps.hosted.exchangeAuthTicket", null,
                    new { ticket, challenge = code, slack_cli_version = SlackCliVersion }, ct);
                var token = String(account, "token");
                var team = String(account, "team_id");
                var user = String(account, "user_id");
                if (!True(account, "is_ready") || True(account, "is_enterprise_install") || !SlackId(team, 'T')
                    || !SlackId(user, 'U') || !SetupToken(token, "xox"))
                    throw new ChatConfigurationException("authorization-installation-unsupported");
                var identity = await provider.SlackSetupAsync("auth.test", token, new { }, ct);
                if (String(identity, "team_id") != team || String(identity, "user_id") != user || True(identity, "is_enterprise_install"))
                    throw new ChatConfigurationException("authorization-installation-unsupported");
                await this.RequireCurrentSetupActorAsync(actor, flow, ct);
                if (await control.ChatConnections.AnyAsync(c => c.ProviderId == "slack" && c.InstallationId == team && c.DeletedAt == null, ct))
                    throw new ChatConfigurationException("configuration-conflict", 409);

                var scopes = SlackScopes.Concat(flow.IncludePrivateChannels ? ["groups:read", "groups:history"] : Array.Empty<string>()).ToArray();
                var events = flow.IncludePrivateChannels ? new[] { "message.channels", "message.groups" } : ["message.channels"];
                var manifest = new
                {
                    _metadata = new { major_version = 1, minor_version = 0 },
                    display_information = new { name = "Forge", description = "Workstream notifications and linked operator replies", background_color = "#24324A" },
                    features = new { bot_user = new { display_name = "Forge", always_online = false } },
                    oauth_config = new { scopes = new { bot = scopes } },
                    settings = new { socket_mode_enabled = true, interactivity = new { is_enabled = true },
                        event_subscriptions = new { bot_events = events }, org_deploy_enabled = false, token_rotation_enabled = false, is_hosted = false },
                };
                var created = await provider.SlackSetupAsync("apps.manifest.create", token, new { manifest }, ct);
                var appId = String(created, "app_id");
                if (!SlackId(appId, 'A')) throw new ChatConfigurationException("authorization-installation-unsupported");
                flow.AppId = appId;
                await this.RequireCurrentSetupActorAsync(actor, flow, ct);
                var installed = await provider.SlackSetupAsync("apps.developerInstall", token,
                    new { app_id = flow.AppId, bot_scopes = scopes }, ct);
                if (String(installed, "app_id") != flow.AppId || !installed.TryGetProperty("api_access_tokens", out var credentials)
                    || !SetupToken(String(credentials, "bot"), "xoxb-") || !SetupToken(String(credentials, "app_level"), "xapp-"))
                    throw new ChatConfigurationException("authorization-installation-unsupported");
                var bot = String(credentials, "bot")!;
                var app = String(credentials, "app_level")!;
                var botIdentity = await provider.SlackSetupAsync("auth.test", bot, new { }, ct);
                if (String(botIdentity, "team_id") != team || !SlackId(String(botIdentity, "bot_id"), 'B') || True(botIdentity, "is_enterprise_install")
                    || String(botIdentity, "app_id") is { } actualApp && actualApp != flow.AppId)
                    throw new ChatConfigurationException("authorization-installation-unsupported");
                var socket = await provider.SlackSetupAsync("apps.connections.open", app, new { }, ct);
                if (!Uri.TryCreate(String(socket, "url"), UriKind.Absolute, out var address) || address.Scheme != "wss"
                    || !address.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase) || address.UserInfo.Length != 0)
                    throw new ChatConfigurationException("authorization-installation-unsupported");
                await this.RequireCurrentSetupActorAsync(actor, flow, ct);
                flow.InstallationId = team;
                flow.AccountName = String(account, "team_name") ?? team;
                flow.BotToken = bot; flow.AppToken = app; flow.Status = "approved";
                // Tooling credentials and refresh tokens remain request-local and are never persisted or returned.
                return flow.View();
            }
            catch (ChatConfigurationException ex) { flow.Fail(ex.Code); return flow.View(); }
            catch { flow.Fail("authorization-denied"); throw; }
        }
        finally { state.Gate.Release(); }
    }

    private async Task RequireCurrentSetupActorAsync(ForgeActor? actor, ChatAuthorizationState.Flow flow, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        if (flow.ExpiresAt <= clock.GetUtcNow()) throw new ChatConfigurationException("authorization-expired", 409);
    }

    private static bool SlackId(string? value, char prefix) => value is { Length: > 1 and <= 32 } && value[0] == prefix && value.All(char.IsAsciiLetterOrDigit);
    private static bool SetupToken(string? value, string prefix) => value is { Length: > 8 and <= 4096 }
        && value.StartsWith(prefix, StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}