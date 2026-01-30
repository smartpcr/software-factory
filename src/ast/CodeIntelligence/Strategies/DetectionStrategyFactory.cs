// -----------------------------------------------------------------------
// <copyright file="DetectionStrategyFactory.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Strategies;

using CodeIntelligence.Models;

/// <summary>
/// Factory for creating detection strategy instances.
/// </summary>
public sealed class DetectionStrategyFactory : IDisposable
{
    private readonly Dictionary<string, IDetectionStrategy> strategies = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionStrategyFactory"/> class.
    /// </summary>
    public DetectionStrategyFactory()
    {
        this.RegisterDefaultStrategies();
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyDictionary<string, IDetectionStrategy> Strategies => this.strategies;

    /// <summary>
    /// Registers default detection strategies.
    /// </summary>
    private void RegisterDefaultStrategies()
    {
        this.RegisterStrategy(new CSharpAstDetectionStrategy());
        this.RegisterStrategy(new ConfigFileDetectionStrategy());
        this.RegisterStrategy(new RegexDetectionStrategy());
    }

    /// <summary>
    /// Registers a detection strategy.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public void RegisterStrategy(IDetectionStrategy strategy)
    {
        this.strategies[strategy.Name] = strategy;
    }

    /// <summary>
    /// Gets a strategy by name.
    /// </summary>
    /// <param name="name">The strategy name.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IDetectionStrategy? GetStrategy(string name)
    {
        return this.strategies.TryGetValue(name, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets the appropriate strategy for a detection pattern.
    /// </summary>
    /// <param name="pattern">The detection pattern.</param>
    /// <returns>The matching strategy, or null if none found.</returns>
    public IDetectionStrategy? GetStrategyForPattern(DetectionPattern pattern)
    {
        foreach (var strategy in this.strategies.Values)
        {
            if (strategy.CanHandle(pattern))
            {
                return strategy;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all strategies that can handle a specific pattern.
    /// </summary>
    /// <param name="pattern">The detection pattern.</param>
    /// <returns>All matching strategies.</returns>
    public IEnumerable<IDetectionStrategy> GetStrategiesForPattern(DetectionPattern pattern)
    {
        return this.strategies.Values.Where(s => s.CanHandle(pattern));
    }

    /// <summary>
    /// Gets all strategies that support a specific language.
    /// </summary>
    /// <param name="language">The language name.</param>
    /// <returns>All matching strategies.</returns>
    public IEnumerable<IDetectionStrategy> GetStrategiesForLanguage(string language)
    {
        return this.strategies.Values.Where(s =>
            s.SupportedLanguages.Contains("*") ||
            s.SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            foreach (var strategy in this.strategies.Values)
            {
                strategy.Dispose();
            }

            this.strategies.Clear();
            this.disposed = true;
        }
    }
}
