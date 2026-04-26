// -----------------------------------------------------------------------
// <copyright file="AdoAuthService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 9.2 — Azure DevOps interactive sign-in via MSAL.
///
/// Uses the well-known Azure CLI public client id so operators don't need to
/// register an Azure AD app to use Forge. WAM broker is the first choice on
/// Windows (picks up the signed-in account silently when the org allows
/// silent issuance). On non-WAM hosts, falls back to system-browser
/// authorization-code + PKCE. Device-code flow is explicitly banned per Q7.
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
    private readonly ILogger<AdoAuthService> logger;
    private readonly Lazy<Task<IPublicClientApplication>> appFactory;

    public AdoAuthService(IOptions<ForgeAuthOptions> options, ILogger<AdoAuthService> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        this.appFactory = new Lazy<Task<IPublicClientApplication>>(this.BuildAppAsync, isThreadSafe: true);
    }

    public string Kind => "ado";

    public async Task<AuthStatus> StatusAsync(CancellationToken ct = default)
    {
        try
        {
            var app = await this.appFactory.Value;
            var account = (await app.GetAccountsAsync()).FirstOrDefault();
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

        // 1) Try silent first against any cached account.
        try
        {
            var account = (await app.GetAccountsAsync()).FirstOrDefault();
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

        // 2) Interactive. WAM-first on Windows; system browser elsewhere.
        try
        {
            var builder = app.AcquireTokenInteractive(DefaultScopes);
            // Hint: prefer the signed-in Windows account silently when possible.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                builder = builder.WithUseEmbeddedWebView(false);
                // Best-effort: pin the WAM popup to the foreground process window
                // so it doesn't open behind everything. WAM uses a PInvoke handle.
                builder = builder.WithParentActivityOrWindow(GetForegroundWindowOrFallback);
            }
            var result = await builder.ExecuteAsync(ct);
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
            var account = (await app.GetAccountsAsync()).FirstOrDefault();
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

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        var clientId = string.IsNullOrWhiteSpace(this.options.AdoClientId) ? AzureCliClientId : this.options.AdoClientId;
        var authority = string.IsNullOrWhiteSpace(this.options.AdoAuthority) ? Authority : this.options.AdoAuthority;

        var builder = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authority)
            // Match the redirect URI the Azure CLI client id is registered with.
            .WithDefaultRedirectUri();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Q7 — WAM is the first choice on Windows.
            builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Forge — sign in to Azure DevOps",
            });
        }

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static IntPtr GetForegroundWindowOrFallback()
    {
        try
        {
            var fg = GetForegroundWindow();
            return fg != IntPtr.Zero ? fg : new IntPtr(-1); // -1 = HWND_DESKTOP fallback
        }
        catch
        {
            return new IntPtr(-1);
        }
    }
}
