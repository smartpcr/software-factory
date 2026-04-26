// -----------------------------------------------------------------------
// <copyright file="AdoAuthService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

/// <summary>
/// Phase 9.2 — Azure DevOps interactive sign-in via MSAL.
///
/// Uses the well-known Azure CLI public client id so operators don't need to
/// register an Azure AD app to use Forge. The flow is system-browser
/// authorization-code: Forge calls <c>AcquireTokenInteractive</c>, MSAL spins
/// up a one-shot localhost listener and shells out to the OS default browser
/// (Edge by default on Windows). Device-code flow is explicitly banned per Q7;
/// WAM broker isn't a fit here because the API runs as a console host with
/// no parent window — see Phase 9 follow-up for re-introducing WAM once the
/// host can publish a window handle reliably.
///
/// Token cache is DPAPI-protected on Windows via MSAL's
/// <see cref="MsalCacheHelper"/>. The cache file lives at
/// <c>%LOCALAPPDATA%\Forge\msal-cache.bin</c>.
/// </summary>
public class AdoAuthService : IAuthService
{
    // Azure DevOps Resource ID — the AAD audience for ADO REST APIs.
    public const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    // Default scope. The Azure CLI app id grants "user_impersonation" on this
    // resource; the .default scope honours whatever was consented to.
    private static readonly string[] DefaultScopes = new[] { $"{AdoResourceId}/.default" };

    // Azure CLI public client id — multi-tenant, broadly available, no app
    // registration needed by the Forge operator. This is the same trick the
    // Azure CLI itself uses for `az login`.
    private const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    // "common" lets MSA + work/school accounts sign in.
    private const string Authority = "https://login.microsoftonline.com/common";

    // Token cache location.
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Forge");

    private const string CacheFileName = "msal-cache.bin";

    private readonly ForgeAuthOptions options;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<AdoAuthService> logger;
    private readonly Lazy<Task<IPublicClientApplication>> appFactory;

    public AdoAuthService(IOptions<ForgeAuthOptions> options, IServiceScopeFactory scopeFactory, ILogger<AdoAuthService> logger)
    {
        this.options = options.Value;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.appFactory = new Lazy<Task<IPublicClientApplication>>(this.BuildAppAsync, isThreadSafe: true);
    }

    public string Kind => "ado";

    public async Task<AuthStatus> StatusAsync(CancellationToken ct = default)
    {
        try
        {
            var app = await this.appFactory.Value;
            var preferred = await this.GetPreferredAccountAsync(ct);
            var account = await this.PickAccountAsync(app, preferred);
            if (account is null)
            {
                return new AuthStatus(this.Kind, Connected: false, Account: null, UserName: null, ExpiresAt: null);
            }
            // Probe silent acquisition so the connected flag actually means
            // "we can call ADO right now" rather than "we have a stale account".
            try
            {
                var result = await app.AcquireTokenSilent(DefaultScopes, account).ExecuteAsync(ct);
                return new AuthStatus(
                    this.Kind,
                    Connected: true,
                    Account: account.Username,
                    UserName: account.Username,
                    ExpiresAt: result.ExpiresOn);
            }
            catch (MsalUiRequiredException)
            {
                // Account is cached but the refresh token is no good.
                return new AuthStatus(this.Kind, Connected: false, Account: account.Username, UserName: account.Username, ExpiresAt: null, Error: "Re-authentication required");
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "AdoAuthService.StatusAsync failed");
            return new AuthStatus(this.Kind, Connected: false, Account: null, UserName: null, ExpiresAt: null, Error: ex.Message);
        }
    }

    public async Task<AuthStatus> SignInAsync(CancellationToken ct = default)
    {
        var app = await this.appFactory.Value;
        var preferred = await this.GetPreferredAccountAsync(ct);

        // 1) Try silent first against the preferred (or first) cached account.
        try
        {
            var account = await this.PickAccountAsync(app, preferred);
            if (account is not null)
            {
                var silent = await app.AcquireTokenSilent(DefaultScopes, account).ExecuteAsync(ct);
                return new AuthStatus(this.Kind, true, account.Username, account.Username, silent.ExpiresOn);
            }
        }
        catch (MsalUiRequiredException)
        {
            // Fall through to interactive.
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Silent acquisition failed; falling back to interactive");
        }

        // 2) Interactive — system browser. Forge runs as a console host with
        // no parent window, so WAM is unreliable from this process; we use the
        // system-browser auth-code flow (MSAL spins up its own localhost listener)
        // and the BrowserLauncher's Edge-first behaviour just falls into place
        // because the browser open call goes through OS shell-execute.
        try
        {
            var builder = app
                .AcquireTokenInteractive(DefaultScopes)
                .WithUseEmbeddedWebView(false);
            if (!string.IsNullOrEmpty(preferred))
            {
                builder = builder.WithLoginHint(preferred);
            }
            this.logger.LogInformation("ADO interactive sign-in starting (system browser){Hint}…",
                string.IsNullOrEmpty(preferred) ? string.Empty : $" with login_hint={preferred}");
            var result = await builder.ExecuteAsync(ct);
            this.logger.LogInformation("ADO interactive sign-in succeeded as {User}", result.Account.Username);
            return new AuthStatus(this.Kind, true, result.Account.Username, result.Account.Username, result.ExpiresOn);
        }
        catch (OperationCanceledException)
        {
            return new AuthStatus(this.Kind, false, null, null, null, "Sign-in cancelled");
        }
        catch (MsalException ex)
        {
            this.logger.LogWarning(ex, "AdoAuthService interactive sign-in failed");
            return new AuthStatus(this.Kind, false, null, null, null, $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "AdoAuthService.SignInAsync hit an unexpected error");
            return new AuthStatus(this.Kind, false, null, null, null, ex.Message);
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var app = await this.appFactory.Value;
        foreach (var account in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(account);
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var app = await this.appFactory.Value;
            var preferred = await this.GetPreferredAccountAsync(ct);
            var account = await this.PickAccountAsync(app, preferred);
            if (account is null) return null;
            var result = await app.AcquireTokenSilent(DefaultScopes, account).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            // Caller decides whether to prompt — surface null so AdoProvider can fall back to PAT.
            return null;
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "GetAccessTokenAsync silent failure");
            return null;
        }
    }

    // -- helpers ----------------------------------------------------------

    /// <summary>
    /// Reads <see cref="UserSettingsRow.PreferredAdoAccount"/> from the
    /// single-user settings row. Returns null if no preference is stored.
    /// </summary>
    private async Task<string?> GetPreferredAccountAsync(CancellationToken ct)
    {
        try
        {
            using var scope = this.scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var row = await db.UserSettings.AsNoTracking()
                .OrderBy(x => x.UserId)
                .FirstOrDefaultAsync(ct);
            var preferred = row?.PreferredAdoAccount?.Trim();
            return string.IsNullOrEmpty(preferred) ? null : preferred;
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Could not read PreferredAdoAccount; falling back to first cached account");
            return null;
        }
    }

    /// <summary>
    /// Picks the cached MSAL account matching <paramref name="preferred"/>
    /// (case-insensitive, by Username = email). Falls back to the first
    /// cached account if no match.
    /// </summary>
    private async Task<IAccount?> PickAccountAsync(IPublicClientApplication app, string? preferred)
    {
        var accounts = await app.GetAccountsAsync();
        if (!string.IsNullOrEmpty(preferred))
        {
            var match = accounts.FirstOrDefault(a => string.Equals(a.Username, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return accounts.FirstOrDefault();
    }

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        var clientId = string.IsNullOrWhiteSpace(this.options.AdoClientId) ? AzureCliClientId : this.options.AdoClientId;
        var authority = string.IsNullOrWhiteSpace(this.options.AdoAuthority) ? Authority : this.options.AdoAuthority;

        // System-browser auth-code flow. The Azure CLI public client id is
        // registered with `http://localhost` as a valid redirect URI, so MSAL
        // can spin up its own one-shot listener on a free port and complete
        // the flow without any window-handle gymnastics.
        var builder = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost");

        var app = builder.Build();

        // Phase 9.5 — DPAPI-protected token cache.
        try
        {
            var storageProps = new StorageCreationPropertiesBuilder(CacheFileName, CacheDirectory)
                .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProps);
            cacheHelper.RegisterCache(app.UserTokenCache);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "MSAL cache helper unavailable — using in-memory cache");
        }

        return app;
    }

}
