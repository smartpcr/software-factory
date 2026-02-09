## 9. Performance

### 9.1 Hot Paths

1. **`GetOrAddAsync`** — called on every API request via `ResourceResolver`. Delegates to `HybridCache.GetOrCreateAsync()` which has L1 fast path.
2. **`GetListFromCacheAsync`** — iterates all item keys in a list mapping, doing N individual `GetFromCacheAsync` calls (sequential). This is an **N+1 pattern** for list reads.

### 9.2 Memory Considerations

- `MemoryCache` size-limited to 100 MB with 5% compaction
- `CsvFileCache.fileLocks` grows unbounded for file paths; cleaned up lazily in `CleanupFileLockIfPossible()` only when `semaphore.CurrentCount == 1`
- `CacheKeyMapping` hash sets grow unbounded for the lifetime of the cache instance
- `ConcurrentBag<string>` in `ClearAsync` (line 622) — allocates per removal

### 9.3 I/O Efficiency

- L2 writes use atomic temp-file-then-rename
- L2 reads use `FileShare.ReadWrite` to allow concurrent access
- File purge runs `Parallel.ForEach` with `MaxDegreeOfParallelism = ProcessorCount * 2`
- Statistics collection in `GetStatistics()` iterates ALL files in the cache folder, calculating sizes in parallel

### 9.4 Scalability

- `HybridCache` is a singleton; safe for multi-instance access
- `CsvFileCache` stores to cluster shared volume — **shared across cluster nodes**. No distributed locking beyond per-process `SemaphoreSlim`. Concurrent writes from multiple nodes could corrupt files (mitigated by atomic writes).
- Background refresh uses `Task.Run` with a timeout CTS — not pool-limited, could spawn many concurrent tasks under load

### 9.5 Initialization

- All caches are initialized eagerly at startup in `UnityConfig.RegisterTypes()` → `RegisterCaching()`
- `DefaultHybridCache<T>` constructor validates `CacheSettings` immediately
- `HybridCacheFactory.CreateCache<T>()` uses lazy `GetOrAdd` — first access creates the instance

---