// -----------------------------------------------------------------------
// <copyright file="ModelRegistry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using System.Text.Json;
using Forge.Api.Contracts;

/// <summary>
/// Phase 11.2 — loads the model catalog from <c>Agents/Models/models.json</c>
/// next to the binary. Failure to read or parse the file is fatal — the
/// runtime needs the catalog to compute cost.
/// </summary>
public sealed class ModelRegistry : IModelRegistry
{
    private const string SeedFileName = "models.json";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Lazy<IReadOnlyList<ModelInfo>> models;

    public ModelRegistry(IHostEnvironment env)
    {
        var contentRoot = env?.ContentRootPath ?? AppContext.BaseDirectory;
        this.models = new Lazy<IReadOnlyList<ModelInfo>>(() => Load(contentRoot));
    }

    public IReadOnlyList<ModelInfo> All => this.models.Value;

    public ModelInfo? Find(string id)
        => this.models.Value.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ModelInfo> Load(string contentRoot)
    {
        // Probe the published location first, fall back to the source tree
        // when running tests from the project's bin/ folder.
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Agents", "Models", SeedFileName),
            Path.Combine(AppContext.BaseDirectory, "Agents", "Models", SeedFileName),
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Model registry seed '{SeedFileName}' not found. Looked in: {string.Join(", ", candidates)}");

        using var stream = File.OpenRead(path);
        var list = JsonSerializer.Deserialize<List<ModelInfo>>(stream, JsonOpts)
            ?? throw new InvalidOperationException($"{SeedFileName} parsed to null");
        return list;
    }
}
