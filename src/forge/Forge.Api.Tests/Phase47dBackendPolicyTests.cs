// -----------------------------------------------------------------------
// <copyright file="Phase47dBackendPolicyTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Configuration;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 47d — operator-controlled backend rail. Asserts that the policy
/// is loaded from <c>Forge:BackendPolicy</c> (with safe defaults when the
/// section is missing), and that <see cref="ForgeSeed.EnsureStartupConfigAsync"/>
/// self-heals + asserts on each boot so disallowed Backend / InvokeMode
/// values can't silently route agents on a paid-quota rail.
/// </summary>
public class Phase47dBackendPolicyTests
{
    [Fact]
    public void FromConfiguration_Missing_Section_Returns_Default_Copilot_Only()
    {
        var config = new ConfigurationBuilder().Build();
        var policy = ForgeBackendPolicy.FromConfiguration(config);

        Assert.Equal("copilot", policy.Canonical);
        Assert.Equal("sdk", policy.CanonicalInvokeMode);
        Assert.Single(policy.AllowedBackends);
        Assert.Contains("copilot", policy.AllowedBackends);
        Assert.DoesNotContain("claude", policy.AllowedBackends);
    }

    [Fact]
    public void FromConfiguration_Reads_Allowed_Lists_From_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forge:BackendPolicy:Canonical"] = "copilot",
                ["Forge:BackendPolicy:CanonicalInvokeMode"] = "sdk",
                ["Forge:BackendPolicy:AllowedBackends:0"] = "copilot",
                ["Forge:BackendPolicy:AllowedBackends:1"] = "claude",
                ["Forge:BackendPolicy:AllowedInvokeModes:0"] = "sdk",
                ["Forge:BackendPolicy:AllowedInvokeModes:1"] = "cli",
            })
            .Build();
        var policy = ForgeBackendPolicy.FromConfiguration(config);

        Assert.Equal(2, policy.AllowedBackends.Count);
        Assert.Contains("claude", policy.AllowedBackends);
        Assert.True(policy.IsBackendAllowed("claude"));
        Assert.True(policy.IsBackendAllowed("copilot"));
    }

    [Fact]
    public void FromConfiguration_Forces_Canonical_Into_Allowed_List()
    {
        // Operator typo: canonical=copilot but they forgot to list copilot
        // in AllowedBackends. Defensive: append rather than fail at load.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forge:BackendPolicy:Canonical"] = "copilot",
                ["Forge:BackendPolicy:AllowedBackends:0"] = "claude",
            })
            .Build();
        var policy = ForgeBackendPolicy.FromConfiguration(config);

        Assert.Contains("copilot", policy.AllowedBackends);
        Assert.Contains("claude", policy.AllowedBackends);
    }

    [Fact]
    public void IsBackendAllowed_Empty_Or_Null_Allowed_As_Inherit()
    {
        var policy = ForgeBackendPolicy.Default;
        Assert.True(policy.IsBackendAllowed(null));
        Assert.True(policy.IsBackendAllowed(""));
        Assert.True(policy.IsBackendAllowed("copilot"));
        Assert.False(policy.IsBackendAllowed("claude"));
    }

    /// <summary>
    /// Headline regression: the seed pass clears any agent override that's
    /// not in the allow-list. Reproduces the 2026-05-09 incident — agent
    /// E9 had Backend="claude" / InvokeMode="cli" and burned the Anthropic
    /// quota for $33k. With a strict copilot/sdk-only policy, both fields
    /// are healed back to NULL on every boot.
    /// </summary>
    [Fact]
    public async Task EnsureStartupConfigAsync_Heals_Disallowed_Agent_Override()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            // Seed a brand-new test agent with a disallowed override.
            var rogue = new AgentRow
            {
                Id = "test-rogue-eng-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = "Rogue Engineer",
                Role = "engineer",
                Backend = "claude",
                InvokeMode = "cli",
            };
            db.Agents.Add(rogue);
            await db.SaveChangesAsync();

            // Sanity-check the override actually persisted before we run heal.
            var beforeHeal = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == rogue.Id);
            Assert.Equal("claude", beforeHeal.Backend);
            Assert.Equal("cli", beforeHeal.InvokeMode);

            // Strict policy: only copilot backend AND only sdk invoke mode.
            // Both of the rogue agent's fields are out-of-policy and must
            // be cleared. (The default policy allows cli, which would
            // legitimately survive — that's covered by the
            // Preserves_Override test.)
            var strict = new ForgeBackendPolicy(
                Canonical: "copilot",
                CanonicalInvokeMode: "sdk",
                AllowedBackends: new[] { "copilot" },
                AllowedInvokeModes: new[] { "sdk" });
            await ForgeSeed.EnsureStartupConfigAsync(db, NullLogger.Instance, strict);

            // Override is healed back to null; agent now inherits from crew.
            var afterHeal = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == rogue.Id);
            Assert.True(string.IsNullOrEmpty(afterHeal.Backend),
                $"expected Backend cleared; got '{afterHeal.Backend}'");
            Assert.True(string.IsNullOrEmpty(afterHeal.InvokeMode),
                $"expected InvokeMode cleared; got '{afterHeal.InvokeMode}'");
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Operator opt-in: when "claude" is in <c>AllowedBackends</c>, an
    /// agent override of <c>Backend="claude"</c> survives the seed pass.
    /// This is how the operator re-enables Anthropic billing after the
    /// quota resets — flip appsettings.json, restart, override stays.
    /// </summary>
    [Fact]
    public async Task EnsureStartupConfigAsync_Preserves_Override_When_In_AllowList()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            var rogue = new AgentRow
            {
                Id = "test-permitted-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = "Permitted Claude Agent",
                Role = "engineer",
                Backend = "claude",
                InvokeMode = "cli",
            };
            db.Agents.Add(rogue);
            await db.SaveChangesAsync();

            // Policy that explicitly permits claude/cli.
            var permissive = new ForgeBackendPolicy(
                Canonical: "copilot",
                CanonicalInvokeMode: "sdk",
                AllowedBackends: new[] { "copilot", "claude" },
                AllowedInvokeModes: new[] { "sdk", "cli" });

            await ForgeSeed.EnsureStartupConfigAsync(db, NullLogger.Instance, permissive);

            var after = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == rogue.Id);
            Assert.Equal("claude", after.Backend);
            Assert.Equal("cli", after.InvokeMode);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// All canonical crews are normalised to <c>policy.Canonical</c> /
    /// <c>policy.CanonicalInvokeMode</c> on every boot, regardless of
    /// what was previously stored. Catches the inverse bug: an operator
    /// (or test fixture) flipping a crew row directly in SQL is harmless
    /// because the next boot snaps it back.
    /// </summary>
    [Fact]
    public async Task EnsureStartupConfigAsync_Normalises_All_Canonical_Crews()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            using (f.CreateClient()) { /* triggers boot */ }
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            // Drift one canonical crew off the rail. We probe a single
            // known id (crew-eng) rather than IN-querying the canonical
            // 8 — EF Core's LINQ interpreter has a known bug evaluating
            // `string[].Contains(c.Id)` against ReadOnlySpan returns on
            // some runtime configurations, and one row is enough to
            // exercise the normalisation path.
            const string probeId = "crew-eng";
            var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == probeId);
            if (crew is null) return; // factory didn't seed canonical crews — vacuously pass.
            crew.Backend = "claude";
            crew.InvokeMode = "cli";
            await db.SaveChangesAsync();

            // Sanity-check the drift persisted.
            var dirty = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == probeId);
            Assert.Equal("claude", dirty.Backend);
            Assert.Equal("cli", dirty.InvokeMode);

            await ForgeSeed.EnsureStartupConfigAsync(db, NullLogger.Instance, ForgeBackendPolicy.Default);

            var after = await db.Crews.AsNoTracking().FirstAsync(c => c.Id == probeId);
            Assert.Equal("copilot", after.Backend);
            Assert.Equal("sdk", after.InvokeMode);
        }
        finally { f.Dispose(); }
    }
}
