// -----------------------------------------------------------------------
// <copyright file="WorkstreamPluginRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Plugins;

using System.Text.Json;
using Common.Chat;

public sealed record WorkstreamPluginDescriptor(string Id, string DisplayName, string ScopeKind,
    int ContractVersion, IReadOnlyList<int> SettingsVersions);

public interface IWorkstreamPlugin
{
    WorkstreamPluginDescriptor Descriptor { get; }
    ChatSettingsValidation ValidateSettings(int version, JsonElement settings);
}

public sealed class WorkstreamPluginRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkstreamPlugin> plugins;
    public WorkstreamPluginRegistry(IEnumerable<IWorkstreamPlugin> plugins)
    {
        var entries = plugins.ToArray();
        this.Errors = Array.AsReadOnly(entries.GroupBy(p => p.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key)).Select(_ => "invalid-or-duplicate-plugin-id").ToArray());
        this.Descriptors = Array.AsReadOnly(entries.Select(p => p.Descriptor).ToArray());
        this.plugins = this.IsAvailable ? entries.Where(p => p.Descriptor.ContractVersion == 1 && p.Descriptor.ScopeKind == "story-spawn")
            .ToDictionary(p => p.Descriptor.Id, StringComparer.Ordinal) : new Dictionary<string, IWorkstreamPlugin>();
    }
    public bool IsAvailable => this.Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<WorkstreamPluginDescriptor> Descriptors { get; }
    public IWorkstreamPlugin? Resolve(string id) => this.plugins.GetValueOrDefault(id);
}