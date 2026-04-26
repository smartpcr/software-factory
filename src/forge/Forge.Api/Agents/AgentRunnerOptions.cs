// -----------------------------------------------------------------------
// <copyright file="AgentRunnerOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.5 — runner configuration. Bound from <c>Forge:Agents:*</c>.
/// </summary>
public sealed class AgentRunnerOptions
{
    public const string SectionName = "Forge:Agents";

    /// <summary>Anthropic API key (kept in user-secrets in dev).</summary>
    public string? AnthropicApiKey { get; set; }

    /// <summary>Anthropic API endpoint. Override for proxies / VCR replay.</summary>
    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Anthropic API version header.</summary>
    public string AnthropicApiVersion { get; set; } = "2023-06-01";

    /// <summary>Default per-invocation wall-clock budget.</summary>
    public TimeSpan WallClockBudget { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Default per-invocation token budget. Null = no token cap (only wall-clock + crew budget apply).</summary>
    public long? DefaultTokenBudget { get; set; }

    /// <summary>Max retries on 429 / 503 / 529.</summary>
    public int MaxRetries { get; set; } = 3;
}
