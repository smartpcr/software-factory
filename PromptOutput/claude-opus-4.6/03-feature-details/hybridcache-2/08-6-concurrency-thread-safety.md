## 6. Concurrency & Thread Safety

### 6.1 Shared Mutable State

| Field | Type | Class | Protection |
|-------|------|-------|-----------|
| `cacheKeys` | `ConcurrentDictionary<CacheKey, byte>` | `DefaultHybridCache<T>` | Lock-free CAS |
| `cacheInstances` | `ConcurrentDictionary<Type, IHybridCache>` | `HybridCacheFactory` | Lock-free CAS |
| `itemToLists` / `listToItems` | `ConcurrentDictionary<CacheKey, HashSet<CacheKey>>` | `CacheKeyMapping` | `lock(syncLock)` for cross-dict operations |
| `fileLocks` | `ConcurrentDictionary<string, SemaphoreSlim>` | `CsvFileCache` | Lock-free CAS + per-file semaphores |
| `_hitCount` / `_missCount` | `long` | `CsvFileCache` | `Interlocked.Increment/Read` |
| `typeSpecificSettings` | `ConcurrentDictionary<Type, JsonSerializerSettings>` | `SerializerFactory` | Lock-free CAS |
| `monitoredActionPlanInstances` | `ConcurrentDictionary<Guid, DateTime>` | `BufferedECEClient` | Lock-free CAS |
| `lastCacheStatsReportTime` | `DateTime?` | `DefaultHybridCache<T>` | **Not synchronized** — benign race (worst case: duplicate stats report) |
| `cacheSettings` | `CacheSettings` | `DefaultHybridCache<T>` | Replaced atomically by `ImportSettings()` |

### 6.2 Synchronization Primitives

| Primitive | Location | Protects |
|-----------|----------|----------|
| `SemaphoreSlim` (per-file) | `CsvFileCache.fileLocks` | Individual file read/write |
| `SemaphoreSlim(1)` | `HealthCheckHandler.systemHealthCheckSempahore` | System-wide health check |
| `ConcurrentDictionary<string, SemaphoreSlim>` | `HealthCheckHandler.healthCheckSemaphores` | Per-update health checks |
| `ReaderWriterLockSlim` | `CsvFileCache.statsLock` | Statistics reading |
| `lock(syncLock)` | `CacheKeyMapping` | Cross-dictionary item-list mapping updates |

### 6.3 Race Conditions

**Potential stampede**: `GetOrAddAsync` delegates to `HybridCache.GetOrCreateAsync()` which has built-in stampede protection (single-flight for same key). However, `GetOrAddListAsync` does NOT use `GetOrCreateAsync` for the list — it does a manual check-then-act (lines 482–529). Multiple concurrent callers for the same list could all call `valueFactory`.

**`lastCacheStatsReportTime`** (line 57): Read/written from any thread without synchronization. Worst case: multiple stats reports in a 5-minute window. Benign.

**`CacheKeyMapping` operations**: Cross-dictionary operations (`UpdateListMappings`, `AddItemToListMapping`) hold `syncLock`. Single-dictionary reads (`GetItemsInList`) do not hold the lock but iterate a `HashSet` which could be modified concurrently — protected by returning `ToList()` snapshots.

### 6.4 Deadlock Risk

**No nested lock acquisitions detected.** `CacheKeyMapping.syncLock` is the only explicit lock. `SemaphoreSlim.WaitAsync()` in `CsvFileCache` is always awaited (no sync-over-async). No async calls inside `lock` blocks.

---