// -----------------------------------------------------------------------
// <copyright file="EmbeddingGeneratorFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Embedding;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Factory for creating embedding generators based on configuration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="EmbeddingGeneratorFactory"/> class.
/// </remarks>
/// <param name="loggerFactory">The logger factory.</param>
public class EmbeddingGeneratorFactory(ILoggerFactory loggerFactory)
{
    private readonly ILoggerFactory loggerFactory = loggerFactory;

    /// <summary>
    /// Creates an embedding generator based on the provided options.
    /// </summary>
    /// <param name="options">The embedding options.</param>
    /// <returns>An embedding generator instance.</returns>
    public IEmbeddingGenerator Create(EmbeddingGeneratorOptions options)
    {
        var wrappedOptions = Options.Create(options);

        return options.Provider switch
        {
            EmbeddingProvider.LLama => new LLamaEmbeddingGenerator(
                wrappedOptions,
                this.loggerFactory.CreateLogger<LLamaEmbeddingGenerator>()),

            EmbeddingProvider.Http or EmbeddingProvider.OpenAI => new HttpEmbeddingGenerator(
                wrappedOptions,
                this.loggerFactory.CreateLogger<HttpEmbeddingGenerator>()),

            _ => throw new ArgumentException($"Unknown provider: {options.Provider}", nameof(options))
        };
    }

    /// <summary>
    /// Creates an embedding generator from AstConfiguration.
    /// </summary>
    /// <param name="config">The AST configuration.</param>
    /// <returns>An embedding generator instance.</returns>
    public IEmbeddingGenerator CreateFromConfig(Ast.Core.EmbeddingConfiguration config)
    {
        var options = new EmbeddingGeneratorOptions
        {
            Provider = EmbeddingProvider.Http, // Default to HTTP for backward compatibility
            Endpoint = config.Endpoint,
            ModelName = config.ModelName,
            Dimensions = config.Dimensions,
            BatchSize = config.BatchSize,
            TimeoutSeconds = config.TimeoutSeconds
        };

        return this.Create(options);
    }
}