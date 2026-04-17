// -----------------------------------------------------------------------
// <copyright file="CapturedLogEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Infrastructure;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// A captured log entry for test assertions.
/// </summary>
public record CapturedLogEntry(
    string CategoryName,
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Scope,
    IReadOnlyList<KeyValuePair<string, object?>> State);

/// <summary>
/// In-memory logger provider that captures log entries with scope data for test assertions.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<CapturedLogEntry> entries = new();

    /// <summary>
    /// Gets all captured log entries.
    /// </summary>
    public IReadOnlyCollection<CapturedLogEntry> Entries => this.entries;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, this.entries);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string categoryName;
        private readonly ConcurrentBag<CapturedLogEntry> entries;
        private readonly AsyncLocal<List<KeyValuePair<string, object?>>> currentScope = new();

        public CapturingLogger(string categoryName, ConcurrentBag<CapturedLogEntry> entries)
        {
            this.categoryName = categoryName;
            this.entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var scopeItems = new List<KeyValuePair<string, object?>>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                scopeItems.AddRange(pairs);
            }

            var previous = this.currentScope.Value;
            this.currentScope.Value = scopeItems;
            return new ScopeDisposable(() => this.currentScope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopeSnapshot = this.currentScope.Value ?? [];
            var stateItems = new List<KeyValuePair<string, object?>>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                stateItems.AddRange(pairs);
            }

            this.entries.Add(new CapturedLogEntry(
                this.categoryName,
                logLevel,
                formatter(state, exception),
                scopeSnapshot.ToList(),
                stateItems));
        }

        private sealed class ScopeDisposable : IDisposable
        {
            private readonly Action onDispose;

            public ScopeDisposable(Action onDispose)
            {
                this.onDispose = onDispose;
            }

            public void Dispose() => this.onDispose();
        }
    }
}
