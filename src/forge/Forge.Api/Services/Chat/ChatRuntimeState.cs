// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeState.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>Single process coordination. A dedicated thread owns the OS mutex across asynchronous work.</summary>
public sealed class ChatRuntimeState(IForgeDbPaths paths) : IDisposable
{
    private readonly object hostLock = new();
    private readonly ManualResetEventSlim stop = new();
    private Thread? mutexThread;
    private bool ownsHost;
    public string ProcessId { get; } = Guid.NewGuid().ToString("N");
    public SemaphoreSlim EnqueueGate { get; } = new(1, 1);
    public SemaphoreSlim DeliveryGate { get; } = new(1, 1);
    public SemaphoreSlim InboxGate { get; } = new(1, 1);
    public SemaphoreSlim DomainGate { get; } = new(1, 1);
    public SemaphoreSlim DispatchGate { get; } = new(1, 1);
    public string? LastScheduledChannel { get; set; }
    public string? LastReconciledDelivery { get; set; }
    public Channel<bool> Wake { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public ConcurrentDictionary<string, ChatOperationCheckpoint> Checkpoints { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, (string Health, string? Reason, long? Sent, long? Received)> Health { get; } = new(StringComparer.Ordinal);
    public long BusyWrites;
    public long CompletedWrites;
    public ConcurrentQueue<double> WriteMilliseconds { get; } = new();

    public void RecordHealth(string connectionId, string health, string? reason, long? sent = null, long? received = null)
        => this.Health.AddOrUpdate(connectionId, (health, reason, sent, received),
            (_, previous) => (health, reason, sent ?? previous.Sent, received ?? previous.Received));

    public bool EnsureHostOwnership()
    {
        lock (this.hostLock)
        {
            if (this.mutexThread is not null) return this.ownsHost;
            using var ready = new ManualResetEventSlim();
            var canonical = Path.GetFullPath(paths.ControlDbPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
            var name = "Forge.Chat." + ChatEventCatalog.Hash(canonical);
            this.mutexThread = new Thread(() =>
            {
                Mutex? mutex = null;
                try
                {
                    mutex = new Mutex(false, name);
                    try { this.ownsHost = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { this.ownsHost = true; }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { this.ownsHost = false; }
                finally { ready.Set(); }
                using (mutex)
                {
                    if (!this.ownsHost) return;
                    this.stop.Wait();
                    mutex!.ReleaseMutex();
                }
            }) { IsBackground = true, Name = "Forge chat ownership" };
            this.mutexThread.Start(); ready.Wait();
            return this.ownsHost;
        }
    }

    public void Signal() => this.Wake.Writer.TryWrite(true);
    public void Dispose()
    {
        this.stop.Set(); this.mutexThread?.Join(TimeSpan.FromSeconds(2));
        this.stop.Dispose();
    }
}

public sealed record ChatOperationCheckpoint(string Kind, string Id, ChatSendReceipt? Send = null,
    ChatConversation? Conversation = null, long Revision = 0);

public sealed class ChatDbBudget(ChatRuntimeState runtime)
{
    public static IDisposable Limit(DbContext db) => new TimeoutScope(db);

    private sealed class TimeoutScope : IDisposable
    {
        private readonly DbContext db;
        private readonly int? commandTimeout;
        private readonly SqliteConnection? sqlite;
        private readonly int defaultTimeout;
        public TimeoutScope(DbContext db)
        {
            this.db = db; this.commandTimeout = db.Database.GetCommandTimeout();
            this.sqlite = db.Database.GetDbConnection() as SqliteConnection;
            this.defaultTimeout = this.sqlite?.DefaultTimeout ?? 0;
            db.Database.SetCommandTimeout(1);
            if (this.sqlite is not null) this.sqlite.DefaultTimeout = 1; // Also bounds BEGIN/COMMIT's internal commands.
        }
        public void Dispose()
        {
            this.db.Database.SetCommandTimeout(this.commandTimeout);
            if (this.sqlite is not null) this.sqlite.DefaultTimeout = this.defaultTimeout;
        }
    }

    public async Task WriteAsync(DbContext db, Func<Task> write, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var limits = Limit(db);
        while (true)
        {
            try
            {
                await write();
                Interlocked.Increment(ref runtime.CompletedWrites);
                runtime.WriteMilliseconds.Enqueue(watch.Elapsed.TotalMilliseconds);
                while (runtime.WriteMilliseconds.Count > 1000) runtime.WriteMilliseconds.TryDequeue(out _);
                return;
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                Interlocked.Increment(ref runtime.BusyWrites);
                if (watch.Elapsed >= TimeSpan.FromMilliseconds(900)) throw;
                await Task.Delay(Random.Shared.Next(20, 60), ct);
            }
        }
    }
    public Task SaveAsync(DbContext db, CancellationToken ct) => this.WriteAsync(db, async () => { await db.SaveChangesAsync(ct); }, ct);
    public static bool IsBusy(Exception ex) => ex is SqliteException { SqliteErrorCode: 5 or 6 }
        || ex.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };
}