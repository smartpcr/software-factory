// -----------------------------------------------------------------------
// <copyright file="AgentProviderSettingsStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Text.Json;
using Forge.Api.Persistence;

/// <summary>
/// Host-wide agent-provider credentials choice: which signed-in GitHub
/// account the shared Copilot server authenticates as. It is host config,
/// not a per-user preference, because one Copilot server serves every agent
/// on this host; and it is independent of the GitHub *repository* account
/// (<c>PreferredGitHubAccount</c>), so agents can bill a different Copilot
/// seat than the one used for issues and PRs.
/// <para>
/// Stored as <c>{Forge:Db:Folder}\agent-providers.json</c> next to the other
/// host files, so no schema change is needed. Holds account names only; tokens
/// stay in gh's OS keyring.
/// </para>
/// </summary>
public sealed class AgentProviderSettingsStore
{
    public const string FileName = "agent-providers.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string path;
    private readonly object gate = new();
    private Snapshot current;

    public AgentProviderSettingsStore(IForgeDbPaths paths)
        : this(Path.Combine(paths.Folder, FileName))
    {
    }

    internal AgentProviderSettingsStore(string path)
    {
        this.path = path;
        this.current = Load(path);
    }

    /// <summary>GitHub login the Copilot server uses, or null to follow the GitHub provider's account.</summary>
    public string? CopilotAccount
    {
        get { lock (this.gate) return this.current.Copilot?.Account; }
    }

    public ProviderChoice? Copilot
    {
        get { lock (this.gate) return this.current.Copilot; }
    }

    public void SetCopilotAccount(string? account, string updatedBy)
    {
        lock (this.gate)
        {
            var next = this.current with
            {
                Copilot = new ProviderChoice(string.IsNullOrWhiteSpace(account) ? null : account.Trim(), DateTimeOffset.UtcNow, updatedBy),
            };
            var dir = Path.GetDirectoryName(this.path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = this.path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, this.path, overwrite: true);
            this.current = next;
        }
    }

    private static Snapshot Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) ?? new Snapshot(null)
                : new Snapshot(null);
        }
        catch (JsonException)
        {
            // A hand-edited, unreadable file must not stop agents: fall back
            // to the default account; the next save rewrites it.
            return new Snapshot(null);
        }
    }

    public sealed record ProviderChoice(string? Account, DateTimeOffset UpdatedAt, string UpdatedBy);

    private sealed record Snapshot(ProviderChoice? Copilot);
}