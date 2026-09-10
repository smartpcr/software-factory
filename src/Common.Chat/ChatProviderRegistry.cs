// -----------------------------------------------------------------------
// <copyright file="ChatProviderRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Collections.ObjectModel;

public sealed record ChatProviderRegistration(ChatProviderDescriptor Descriptor, bool Available, string? Reason);
public interface IChatProviderRegistry
{
    bool IsAvailable { get; }
    IReadOnlyList<string> Errors { get; }
    IReadOnlyList<ChatProviderRegistration> Providers { get; }
    IChatProviderPlugin? Resolve(string id);
}

/// <summary>Ambiguity disables this registry, never application startup. Version failures are scoped.</summary>
public sealed class ChatProviderRegistry : IChatProviderRegistry
{
    public const int ContractVersion = 1;
    private readonly IReadOnlyDictionary<string, IChatProviderPlugin> plugins;

    public ChatProviderRegistry(IEnumerable<IChatProviderPlugin> providers)
    {
        var entries = providers.ToArray();
        this.Errors = Array.AsReadOnly(entries.GroupBy(p => p.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1 || string.IsNullOrWhiteSpace(g.Key))
            .Select(_ => "invalid-or-duplicate-provider-id").ToArray());
        this.Providers = Array.AsReadOnly(entries.Select(p => new ChatProviderRegistration(p.Descriptor,
            this.IsAvailable && p.Descriptor.ContractVersion == ContractVersion,
            !this.IsAvailable ? "registry-unavailable" : p.Descriptor.ContractVersion != ContractVersion ? "unsupported-provider-version" : null)).ToArray());
        this.plugins = new ReadOnlyDictionary<string, IChatProviderPlugin>(this.IsAvailable
            ? entries.Where(p => p.Descriptor.ContractVersion == ContractVersion).ToDictionary(p => p.Descriptor.Id, StringComparer.Ordinal)
            : new Dictionary<string, IChatProviderPlugin>(StringComparer.Ordinal));
    }

    public bool IsAvailable => this.Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<ChatProviderRegistration> Providers { get; }
    public IChatProviderPlugin? Resolve(string id) => this.plugins.GetValueOrDefault(id);
}