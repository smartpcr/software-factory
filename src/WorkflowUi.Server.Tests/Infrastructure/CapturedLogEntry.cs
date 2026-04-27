// -----------------------------------------------------------------------
// <copyright file="CapturedLogEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Infrastructure;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

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
/// Optionally forwards log output to an <see cref="ITestOutputHelper"/> sink.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<CapturedLogEntry> entries = new();
    private volatile ITestOutputHelper? outputHelper;

    /// <summary>
    /// Gets all captured log entries.
    /// </summary>
    public IReadOnlyCollection<CapturedLogEntry> Entries => this.entries;

    /// <summary>
    /// Attaches an <see cref="ITestOutputHelper"/> sink so captured log entries
    /// are also written to xunit test output.
    /// </summary>
    public void SetTestOutputHelper(ITestOutputHelper output)
    {
        this.outputHelper = output;
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, this.entries, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string categoryName;
        private readonly ConcurrentBag<CapturedLogEntry> entries;
        private readonly CapturingLoggerProvider provider;
        private readonly AsyncLocal<List<KeyValuePair<string, object?>>> currentScope = new();

        public CapturingLogger(string categoryName, ConcurrentBag<CapturedLogEntry> entries, CapturingLoggerProvider provider)
        {
            this.categoryName = categoryName;
            this.entries = entries;
            this.provider = provider;
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
            return new ScopeDisposable(() => this.currentScope.Value = previous!);
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

            var message = formatter(state, exception);

            this.entries.Add(new CapturedLogEntry(
                this.categoryName,
                logLevel,
                message,
                scopeSnapshot.ToList(),
                stateItems));

            // Forward to ITestOutputHelper sink if attached, including structured fields
            try
            {
                var helper = this.provider.outputHelper;
                if (helper is not null)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[{logLevel}] {this.categoryName}: {message}");

                    // Append scope fields (e.g. correlationId, method, path)
                    if (scopeSnapshot.Count > 0)
                    {
                        sb.Append(" | scope: {");
                        sb.Append(string.Join(", ", scopeSnapshot.Select(kv => $"{kv.Key}={kv.Value}")));
                        sb.Append('}');
                    }

                    // Append state fields (e.g. StatusCode, ElapsedMs)
                    if (stateItems.Count > 0)
                    {
                        sb.Append(" | state: {");
                        sb.Append(string.Join(", ", stateItems.Select(kv => $"{kv.Key}={kv.Value}")));
                        sb.Append('}');
                    }

                    helper.WriteLine(sb.ToString());
                }
            }
            catch (InvalidOperationException)
            {
                // ITestOutputHelper throws when test is no longer active
            }
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
