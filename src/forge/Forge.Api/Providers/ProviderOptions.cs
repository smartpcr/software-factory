// -----------------------------------------------------------------------
// <copyright file="ProviderOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Providers;

/// <summary>Bound from the <c>Forge:Ado</c> configuration section.</summary>
public class AdoOptions
{
    public const string SectionName = "Forge:Ado";

    /// <summary>Org URL such as <c>https://dev.azure.com/contoso</c>.</summary>
    public string OrgUrl { get; set; } = string.Empty;

    /// <summary>Project name within the org.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>Repository name (used for PRs).</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Personal Access Token. Read from user-secrets / env in dev.</summary>
    public string Pat { get; set; } = string.Empty;

    /// <summary>Optional WIQL clause to narrow which work items get pulled.</summary>
    public string? WorkItemQuery { get; set; }

    /// <summary>Shared secret used to verify ADO Service Hook bodies.</summary>
    public string? WebhookSecret { get; set; }
}

/// <summary>Bound from the <c>Forge:GitHub</c> configuration section.</summary>
public class GitHubOptions
{
    public const string SectionName = "Forge:GitHub";

    /// <summary>"owner/repo" (e.g. <c>microsoft/forge</c>).</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>Personal Access Token (classic or fine-grained).</summary>
    public string Pat { get; set; } = string.Empty;

    /// <summary>Optional override (defaults to https://api.github.com).</summary>
    public string ApiBase { get; set; } = "https://api.github.com";

    /// <summary>Shared secret used by GitHub's X-Hub-Signature-256 header.</summary>
    public string? WebhookSecret { get; set; }
}

/// <summary>Polling cadence + jitter for the background sync worker.</summary>
public class ProviderSyncOptions
{
    public const string SectionName = "Forge:Sync";

    /// <summary>Default polling interval (seconds).</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>±jitter applied to each interval to avoid thundering-herd polling.</summary>
    public int JitterSeconds { get; set; } = 10;

    /// <summary>If a webhook hasn't arrived for this many seconds, polling resumes.</summary>
    public int WebhookSilenceFallbackSeconds { get; set; } = 300;

    /// <summary>Set to false to disable polling entirely (tests).</summary>
    public bool Enabled { get; set; } = true;
}
