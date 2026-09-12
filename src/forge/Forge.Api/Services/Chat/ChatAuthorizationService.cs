// -----------------------------------------------------------------------
// <copyright file="ChatAuthorizationService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static ChatAuthorizationProvider;

public sealed partial class ChatAuthorizationService(ChatAuthorizationState state, ChatAuthorizationProvider provider,
    IOptions<ChatPluginOptions> options, ChatSecretResolver secrets, IEnumerable<IChatSecretStore> stores,
    ForgeCapabilityAuthorizer authorizer, ChatConfigurationService configuration, ControlDbContext control,
    ChatLegacySource legacy, TimeProvider clock)
{
    private ChatAuthorizationOptions Options => options.Value.Authorization;
    // A stable protected slot makes first-time registration survive restart without a config-file edit.
    internal static readonly ChatSecretReference LocalTelegramManager = new("dpapi", "chat-authorization-telegram-manager");
    private ChatSecretReference ManagerReference => this.Options.Telegram.ManagerToken ?? LocalTelegramManager;

    public async Task<IReadOnlyList<ChatAuthorizationAvailability>> AvailabilityAsync(ForgeActor? actor, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var result = new List<ChatAuthorizationAvailability>();
        foreach (var id in new[] { "slack", "telegram" })
        {
            var reason = await this.RegistrationReasonAsync(id, ct);
            result.Add(new(id, reason is null, reason, reason == "authorization-registration-required" &&
                (id == "telegram" && this.Options.Telegram.ManagerToken is null || id == "slack" && this.CanConfigureSlack)));
        }
        return result;
    }

    public async Task<ChatTelegramManagerView> ConfigureTelegramAsync(ForgeActor? actor, ChatTelegramManagerSetup request, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 256 || token.Count(c => c == ':') != 1
            || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-')
            || !long.TryParse(token.Split(':')[0], out var id) || id <= 0 || token.EndsWith(':'))
            throw new ChatConfigurationException("authorization-manager-token-invalid", 400);
        await state.Gate.WaitAsync(ct);
        try
        {
            // Host-owned external references take precedence and cannot be overwritten by this first-time setup.
            if (this.Options.Telegram.ManagerToken is not null)
                throw new ChatConfigurationException("authorization-manager-externally-configured", 409);
            var store = this.WritableStore();
            var existing = await store.ReadAsync(LocalTelegramManager, ct);
            if (existing is not null && existing != token)
                throw new ChatConfigurationException("authorization-manager-already-configured", 409);
            var username = await this.ValidateManagerAsync(token, ct);
            // Provider checks can take time; a revoked administrator must not persist a credential.
            await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
            if (existing is null)
            {
                try { await store.WriteAsync(LocalTelegramManager.Key, token, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                { throw new ChatConfigurationException("authorization-manager-save-failed", 503); }
                state.TelegramOffset = 0;
                state.TelegramNextPoll = default;
            }
            // Same-token retries are idempotent, including a lost response after the atomic write.
            return new(username);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ChatAuthorizationView> BeginAsync(ForgeActor? actor, ChatAuthorizationStart request, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        if (request.ProviderId is not ("slack" or "telegram")) throw new ChatConfigurationException("provider-unavailable", 400);
        if (await this.RegistrationReasonAsync(request.ProviderId, ct) is { } reason) throw new ChatConfigurationException(reason, 409);
        await state.Gate.WaitAsync(ct);
        try
        {
            // Check the dedicated manager before issuing a flow; never steal delivery/legacy updates.
            var manager = request.ProviderId == "telegram" ? await this.ManagerAsync(ct) : null;
            var flow = state.Create(actor!, request);
            if (request.ProviderId == "slack")
            {
                var scopes = SlackScopes.Concat(request.IncludePrivateChannels ? ["groups:read", "groups:history"] : Array.Empty<string>());
                flow.AuthorizationUrl = QueryHelpers.AddQueryString("https://slack.com/oauth/v2/authorize", new Dictionary<string, string?>
                {
                    ["client_id"] = this.Options.Slack.ClientId, ["scope"] = string.Join(',', scopes),
                    ["redirect_uri"] = this.Options.Slack.RedirectUri, ["state"] = flow.State,
                });
            }
            else
            {
                // Telegram permits editing the name. The random username is the correlation capability.
                // 112 bits of entropy, 32 characters, valid Telegram bot username ending in "bot".
                flow.BotUsername = "f" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(14)).ToLowerInvariant() + "bot";
                flow.AuthorizationUrl = "https://t.me/newbot/" + manager + "/" + flow.BotUsername + "?name=Forge";
            }
            return flow.View();
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Anonymous callback records a single code only. Authenticated polling exchanges it; save needs CSRF and a current host administrator.</summary>
    public async Task<bool> CallbackAsync(string? nonce, string? code, string? error, CancellationToken ct)
    {
        if (nonce?.Length != 64 || code?.Length > 2048) return false;
        await state.Gate.WaitAsync(ct);
        try
        {
            state.Prune();
            var flow = state.Flows.Values.SingleOrDefault(f => f.ProviderId == "slack" && f.State == nonce);
            if (flow is null || flow.Status != "waiting" || flow.Code is not null) return false;
            if (error is not null) flow.Fail("authorization-denied");
            else if (string.IsNullOrWhiteSpace(code) || code.Any(char.IsControl)) return false;
            else flow.Code = code;
            return true;
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ChatAuthorizationView> PollAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            var flow = state.Owned(actor!, id);
            if (flow.Status == "waiting")
            {
                try
                {
                    if (flow.ProviderId == "slack" && flow.Code is { } code)
                    {
                        flow.Code = null;
                        // Never retry this one-time code, including after a timeout of uncertain outcome.
                        flow.Status = "exchanging";
                        var result = await provider.SlackAsync(this.Options.Slack, await this.SecretAsync(this.Options.Slack.ClientSecret, ct), code, ct);
                        var requiredScopes = SlackScopes.Concat(flow.IncludePrivateChannels ? ["groups:read", "groups:history"] : Array.Empty<string>());
                        var scopes = (String(result, "scope") ?? "").Split(',').ToHashSet(StringComparer.Ordinal);
                        if (String(result, "app_id") != this.Options.Slack.AppId || String(result, "token_type") != "bot"
                            || True(result, "is_enterprise_install") || !requiredScopes.All(scopes.Contains)
                            || result.TryGetProperty("expires_in", out _) || result.TryGetProperty("refresh_token", out _)
                            || !result.TryGetProperty("team", out var team) || String(team, "id") is not { } teamId
                            || teamId.Length > 32 || !teamId.StartsWith('T') || !teamId.All(char.IsAsciiLetterOrDigit)
                            || String(result, "access_token") is not { } token || !token.StartsWith("xoxb-", StringComparison.Ordinal)
                            || token.Length > 4096 || token.Any(char.IsControl))
                            throw new ChatConfigurationException("authorization-installation-unsupported");
                        flow.InstallationId = teamId;
                        flow.AccountName = String(team, "name") ?? teamId;
                        flow.BotToken = token;
                        flow.Status = "approved";
                        flow.AuthorizationUrl = null;
                    }
                    else if (flow.ProviderId == "telegram") await this.PollTelegramAsync(ct);
                }
                catch (ChatConfigurationException ex) { flow.Fail(ex.Code); }
            }
            if (flow.ExpiresAt <= clock.GetUtcNow()) { flow.Fail("authorization-expired"); }
            return flow.View();
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ChatAuthorizationView> CancelAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            var flow = state.Owned(actor!, id);
            if (flow.Status != "saved") { flow.Status = "cancelled"; flow.Clear(); }
            return flow.View();
        }
        finally { state.Gate.Release(); }
    }

    public async Task<ChatAuthorizationView> SaveAsync(ForgeActor? actor, string id, ChatAuthorizationSave request, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await state.Gate.WaitAsync(ct);
        try
        {
            var flow = state.Owned(actor!, id);
            if (flow.Status == "saved") return flow.View(); // Retrying a lost HTTP response must not create a second installation.
            if (flow.Status != "approved" || flow.BotToken is null) throw new ChatConfigurationException("authorization-not-approved", 409);
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 512 || request.DisplayName.Any(char.IsControl))
                throw new ChatConfigurationException("invalid-text");
            if (request.ProductIds is null || request.ProductIds.Count > 256 || request.ProductIds.Distinct(StringComparer.Ordinal).Count() != request.ProductIds.Count
                || await control.Products.CountAsync(p => request.ProductIds.Contains(p.Id), ct) != request.ProductIds.Count)
                throw new ChatConfigurationException("invalid-product-grants");
            if (await control.ChatConnections.AnyAsync(c => c.ProviderId == flow.ProviderId && c.InstallationId == flow.InstallationId && c.DeletedAt == null, ct))
                throw new ChatConfigurationException("configuration-conflict", 409);
            var store = this.WritableStore();
            var reference = await store.WriteAsync("chat-" + Guid.NewGuid().ToString("N"), flow.BotToken, ct);
            var connectionId = "authorized-" + flow.Id;
            ChatSecretReference? appReference = null;
            try
            {
                var references = new Dictionary<string, ChatSecretReference> { ["bot-token"] = reference };
                if (flow.ProviderId == "slack")
                {
                    if (flow.SetupMode == "slack-cli")
                    {
                        if (flow.AppToken is null || flow.AppId is null) throw new ChatConfigurationException("authorization-not-approved", 409);
                        appReference = await store.WriteAsync("chat-" + Guid.NewGuid().ToString("N"), flow.AppToken, ct);
                    }
                    references.Add("app-token", appReference ?? this.Options.Slack.AppToken!);
                }
                var settings = flow.ProviderId == "slack"
                    ? JsonSerializer.SerializeToElement(new { appId = flow.AppId ?? this.Options.Slack.AppId, includePrivateChannels = flow.IncludePrivateChannels })
                    : JsonSerializer.SerializeToElement(new { });
                var saved = await configuration.PutConnectionAsync(actor, connectionId, new(0, flow.ProviderId, flow.InstallationId!,
                    request.DisplayName.Trim(), true, 1, settings, references, request.ProductIds), ct);
                flow.ConnectionId = saved.Id; flow.Status = "saved"; flow.Clear();
                return flow.View();
            }
            catch
            {
                // A cancelled commit may have reached SQLite. Never delete a blob a committed row uses.
                // If reconciliation itself fails, leave the encrypted blob for an operator to reconcile.
                var persisted = await control.ChatConnections.AsNoTracking().Where(c => c.Id == connectionId)
                    .Select(c => c.SecretReferencesJson).SingleOrDefaultAsync(CancellationToken.None);
                if (persisted is not null && JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(persisted)?.GetValueOrDefault("bot-token") == reference)
                {
                    flow.ConnectionId = connectionId; flow.Status = "saved"; flow.Clear();
                    return flow.View();
                }
                await store.DeleteAsync(reference, CancellationToken.None);
                if (appReference is not null) await store.DeleteAsync(appReference, CancellationToken.None);
                throw;
            }
        }
        finally { state.Gate.Release(); }
    }

    private async Task<string?> RegistrationReasonAsync(string id, CancellationToken ct)
    {
        if (!stores.Any(s => s.Id == "dpapi" && s.Status.Available && !s.Status.ExternallyManaged)) return "credential-store-unavailable";
        var slack = this.Options.Slack;
        if (id == "slack" && (string.IsNullOrWhiteSpace(slack.ClientId) || string.IsNullOrWhiteSpace(slack.AppId)
            || slack.ClientSecret is null || slack.AppToken is null)) return "authorization-registration-required";
        if (id == "slack" && (!Uri.TryCreate(slack.RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/chat/authorize/slack")) return "authorization-https-required";
        var refs = id == "slack" ? new[] { slack.ClientSecret, slack.AppToken } : [this.ManagerReference];
        foreach (var reference in refs)
            if (reference is null || string.IsNullOrWhiteSpace(await secrets.ReadAsync(reference, ct))) return "authorization-registration-required";
        return null;
    }

    private IChatSecretStore WritableStore() => stores.SingleOrDefault(s => s.Id == "dpapi" && s.Status.Available && !s.Status.ExternallyManaged)
        ?? throw new ChatConfigurationException("credential-store-unavailable", 409);
    private async Task<string> SecretAsync(ChatSecretReference? reference, CancellationToken ct)
        => reference is not null && await secrets.ReadAsync(reference, ct) is { Length: > 0 } value ? value
            : throw new ChatConfigurationException("authorization-registration-required", 409);

    private async Task<string> ManagerAsync(CancellationToken ct)
        => await this.ValidateManagerAsync(await this.SecretAsync(this.ManagerReference, ct), ct);

    private async Task<string> ValidateManagerAsync(string token, CancellationToken ct)
    {
        var identity = await provider.TelegramAsync(token, "getMe", new { }, ct);
        if (!identity.TryGetProperty("result", out var bot) || !True(bot, "is_bot") || !True(bot, "can_manage_bots")
            || Number(bot, "id") is not { } botId || String(bot, "username") is not { } name
            || name.Length is < 5 or > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || token.Split(':')[0] != botId)
            throw new ChatConfigurationException("authorization-manager-required", 409);
        var legacyToken = await legacy.ReadAsync(legacy.Reference, ct);
        if (legacyToken?.Split(':')[0] == botId || await control.ChatConnections.AnyAsync(c => c.ProviderId == "telegram" && c.InstallationId == botId && c.DeletedAt == null, ct))
            throw new ChatConfigurationException("authorization-manager-dedicated", 409);
        var webhook = await provider.TelegramAsync(token, "getWebhookInfo", new { }, ct);
        if (!webhook.TryGetProperty("result", out var info) || String(info, "url") != "")
            throw new ChatConfigurationException("authorization-manager-webhook", 409);
        return name;
    }

    private async Task PollTelegramAsync(CancellationToken ct)
    {
        if (state.TelegramNextPoll > clock.GetUtcNow()) return;
        state.TelegramNextPoll = clock.GetUtcNow().AddSeconds(3);
        await this.ManagerAsync(ct);
        var token = await this.SecretAsync(this.ManagerReference, ct);
        var response = await provider.TelegramAsync(token, "getUpdates", new { offset = state.TelegramOffset, timeout = 1, limit = 100, allowed_updates = new[] { "managed_bot" } }, ct);
        if (!response.TryGetProperty("result", out var updates) || updates.ValueKind != JsonValueKind.Array)
            throw new ChatConfigurationException("authorization-provider-rejected");
        foreach (var update in updates.EnumerateArray())
        {
            if (!update.TryGetProperty("update_id", out var rawId) || !rawId.TryGetInt64(out var updateId) || updateId < state.TelegramOffset) continue;
            if (update.TryGetProperty("managed_bot", out var managed) && managed.TryGetProperty("bot", out var bot)
                && Number(bot, "id") is { } botId && String(bot, "username") is { } username && True(bot, "is_bot"))
            {
                var flow = state.Flows.Values.SingleOrDefault(f => f.ProviderId == "telegram" && f.Status == "waiting"
                    && f.ExpiresAt > clock.GetUtcNow() && string.Equals(f.BotUsername, username, StringComparison.OrdinalIgnoreCase));
                if (flow is not null)
                {
                    try
                    {
                        var credential = await provider.TelegramAsync(token, "getManagedBotToken", new { user_id = long.Parse(botId, System.Globalization.CultureInfo.InvariantCulture) }, ct);
                        var botToken = String(credential, "result") ?? throw new ChatConfigurationException("authorization-provider-rejected");
                        var identity = await provider.TelegramAsync(botToken, "getMe", new { }, ct);
                        if (!identity.TryGetProperty("result", out var verified) || Number(verified, "id") != botId || !True(verified, "is_bot")
                            || !string.Equals(String(verified, "username"), flow.BotUsername, StringComparison.OrdinalIgnoreCase))
                            throw new ChatConfigurationException("authorization-provider-rejected");
                        flow.BotToken = botToken; flow.InstallationId = botId; flow.AccountName = "@" + username;
                        flow.Status = "approved"; flow.AuthorizationUrl = null;
                    }
                    catch (ChatConfigurationException ex) { flow.Fail(ex.Code); }
                }
            }
            state.TelegramOffset = updateId + 1;
        }
    }
}