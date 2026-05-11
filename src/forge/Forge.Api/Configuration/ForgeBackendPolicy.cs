// -----------------------------------------------------------------------
// <copyright file="ForgeBackendPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Configuration;

using Microsoft.Extensions.Configuration;

/// <summary>
/// Phase 47d — operator-controlled rail for which agent backends and
/// invoke-modes are permitted in this Forge instance. Loaded from the
/// <c>Forge:BackendPolicy</c> section of <c>appsettings.json</c> (env vars
/// override). Lives in <see cref="IConfiguration"/> rather than as a
/// constant so the operator can re-enable a backend without a code change
/// — e.g. flip <c>"AllowedBackends": ["copilot", "claude"]</c> to bring
/// Anthropic billing back when the cap resets, then bounce Forge.Api.
///
/// <para>Default schema (also the safe-by-default values applied when the
/// section is missing from config):</para>
/// <code>
/// "Forge": {
///   "BackendPolicy": {
///     "Canonical": "copilot",                 // crews are pinned to this
///     "CanonicalInvokeMode": "sdk",
///     "AllowedBackends":    [ "copilot" ],    // anything outside this list is cleared on boot
///     "AllowedInvokeModes": [ "sdk", "cli" ]
///   }
/// }
/// </code>
///
/// <para>Two enforcement passes use this policy on every boot
/// (<see cref="Forge.Api.Persistence.ForgeSeed.EnsureStartupConfigAsync"/>):
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       Self-heal — pin canonical crews to <see cref="Canonical"/> /
///       <see cref="CanonicalInvokeMode"/>; clear any agent-level
///       Backend/InvokeMode override that isn't in
///       <see cref="AllowedBackends"/> / <see cref="AllowedInvokeModes"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       Hard assert — after the heal, refuse to start if any row is
///       still non-conforming. Silent drift is exactly what cost the
///       operator the Anthropic quota burn on 2026-05-09.
///     </description>
///   </item>
/// </list>
/// </summary>
public sealed record ForgeBackendPolicy(
    string Canonical,
    string CanonicalInvokeMode,
    IReadOnlyList<string> AllowedBackends,
    IReadOnlyList<string> AllowedInvokeModes)
{
    /// <summary>The path under <c>IConfiguration</c> where this section lives.</summary>
    public const string SectionName = "Forge:BackendPolicy";

    /// <summary>
    /// Hard-coded fallback applied when no <c>Forge:BackendPolicy</c>
    /// section exists in config. Conservative — copilot only — so a
    /// fresh deployment can't accidentally route agents on a paid
    /// Anthropic rail without explicit operator opt-in. The defaults
    /// match the values the operator intended on 2026-05-09 after the
    /// E9-override incident; they are the "safe" failure mode, not a
    /// silent reintroduction of the bug.
    /// </summary>
    public static ForgeBackendPolicy Default { get; } = new(
        Canonical: "copilot",
        CanonicalInvokeMode: "sdk",
        AllowedBackends: new[] { "copilot" },
        AllowedInvokeModes: new[] { "sdk", "cli" });

    /// <summary>Read the policy from configuration, falling back to <see cref="Default"/>.</summary>
    public static ForgeBackendPolicy FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var section = config.GetSection(SectionName);
        if (!section.Exists()) return Default;

        var canonical = NonEmpty(section["Canonical"]) ?? Default.Canonical;
        var canonicalInvoke = NonEmpty(section["CanonicalInvokeMode"]) ?? Default.CanonicalInvokeMode;

        var allowedBackends = ReadList(section, "AllowedBackends", Default.AllowedBackends);
        var allowedInvokeModes = ReadList(section, "AllowedInvokeModes", Default.AllowedInvokeModes);

        // The canonical value MUST be allowed — otherwise the seed pass
        // would just immediately fail its own assertion. Defensive: add it
        // to the allowed list rather than throwing here, so a typo in
        // appsettings.json doesn't brick startup.
        if (!ContainsIgnoreCase(allowedBackends, canonical))
        {
            allowedBackends = allowedBackends.Concat(new[] { canonical }).ToArray();
        }
        if (!ContainsIgnoreCase(allowedInvokeModes, canonicalInvoke))
        {
            allowedInvokeModes = allowedInvokeModes.Concat(new[] { canonicalInvoke }).ToArray();
        }

        return new ForgeBackendPolicy(canonical, canonicalInvoke, allowedBackends, allowedInvokeModes);
    }

    /// <summary>True when <paramref name="backend"/> (case-insensitive, null-tolerant) is permitted.</summary>
    public bool IsBackendAllowed(string? backend)
    {
        if (string.IsNullOrEmpty(backend)) return true; // empty = inherit from crew; allowed.
        return ContainsIgnoreCase(this.AllowedBackends, backend);
    }

    /// <summary>True when <paramref name="invokeMode"/> (case-insensitive, null-tolerant) is permitted.</summary>
    public bool IsInvokeModeAllowed(string? invokeMode)
    {
        if (string.IsNullOrEmpty(invokeMode)) return true;
        return ContainsIgnoreCase(this.AllowedInvokeModes, invokeMode);
    }

    private static IReadOnlyList<string> ReadList(IConfigurationSection section, string key, IReadOnlyList<string> fallback)
    {
        var sub = section.GetSection(key);
        if (!sub.Exists()) return fallback;
        var values = sub.GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();
        return values.Length == 0 ? fallback : values;
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsIgnoreCase(IReadOnlyList<string> haystack, string needle)
    {
        for (int i = 0; i < haystack.Count; i++)
        {
            if (string.Equals(haystack[i], needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
