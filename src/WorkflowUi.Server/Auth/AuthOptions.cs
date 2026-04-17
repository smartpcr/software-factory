// -----------------------------------------------------------------------
// <copyright file="AuthOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Auth;

/// <summary>
/// Configuration model for authentication settings, bound from "WorkflowUi:Auth".
/// </summary>
public class AuthOptions
{
    public const string SectionName = "WorkflowUi:Auth";

    /// <summary>OIDC authority URL (e.g. Azure AD tenant).</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Expected audience (aud) claim in JWT tokens.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>When true AND env=Development AND request is loopback, auto-authenticate.</summary>
    public bool AllowDevBypass { get; set; }

    /// <summary>JWKS endpoint URL for token validation.</summary>
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>Allowed clock skew in seconds for token expiration validation.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
