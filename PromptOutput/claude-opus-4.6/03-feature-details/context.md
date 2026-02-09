### 1. Architecture Overview

The HybridCache system is a two-tier cache-aside pattern for the Azure Stack HCI Update Resource Provider. It wraps Microsoft's `Microsoft.Extensions.Caching.Hybrid.HybridCache` with domain-specific list management, background refresh, and event-driven invalidation.

**Tiers:**
- **L1 (Local):** `Microsoft.Extensions.Caching.Memory.MemoryCache` — in-process, object references, no serialization.
- **L2 (Distributed):** `CsvFileCache` (implements `IDistributedCache`) — on-disk CSV files, requires serialization via `SerializerFactory`.
- **Orchestrator:** Microsoft's `HybridCache` — manages L1↔L2 promotion/demotion, serialization at the L2 boundary.
- **Application Wrapper:** `DefaultHybridCache<T>` — adds list decomposition, key mapping, background refresh, statistics, and settings management.

**Lifecycle:** Settings.xml → configDict → `CacheSettings.GetCacheSettings<T>()` → `HybridCacheFactory.CreateCache<T>()` → `DefaultHybridCache<T>` → consumed by `BufferedECEClient` / `AutoCacheECEResult<T>`.

### 2. Key Components

#### 2.1 Interfaces

| Interface | File | Purpose |
|-----------|------|---------|
| `IHybridCache` | `IHybridCache.cs:17-23` | Non-generic: stats, settings export/import |
| `IHybridCache<T>` | `IHybridCache.cs:25-42` | Generic item+list ops; `T : ICachedItem` |
| `IListHybridCache<T>` | `IListHybridCache.cs:15-32` | List-specific subset |
| `IHybridCacheFactory` | `IHybridCacheFactory.cs:15-65` | Creates/retrieves typed cache singletons |
| `ICachedItem` | (not provided) | Constraint: `GetCacheKey()`, `GetLastModifiedTime()` |
| `ICacheKeyMapping` | (contract project) | Bidirectional list↔item key tracking |

#### 2.2 Core Classes

**`DefaultHybridCache<T>`** (`DefaultHybridCache.cs:33`) — The main implementation. Key fields:
- `hybridCache` (Microsoft `HybridCache`) — inner L1+L2 engine.
- `cacheKeyMapping` (`CacheKeyMapping`) — tracks which items belong to which lists.
- `cacheKeys` (`ConcurrentDictionary<CacheKey, byte>`) — all known keys for enumeration/clear.
- `cacheEntryOptions` / `readonlyCacheEntryOptions` — pre-built options controlling L1/L2 flags.
- `isDisabled` — runtime kill switch from `CacheSettings.IsDisabled`.

**`HybridCacheFactory`** (`HybridCacheFactory.cs:17`) — Singleton-per-type registry via `ConcurrentDictionary<Type, IHybridCache> cacheInstances`. Methods:
- `CreateCache<T>(ttl, timeout?)` — `GetOrAdd` pattern; calls `PopulateConfig<T>()` then constructs `DefaultHybridCache<T>`.
- `TryGet<T>()` / `TryGet(string)` — lookup by type or type name (used by cache controller endpoints).
- `PopulateConfig<T>()` — writes TTL and background refresh keys into `configDict`.

**`AutoCacheECEResult<T>`** (`AutoCacheECEResult.cs`) — Thin wrapper: `GetValueAsync` → `cache.GetOrAddAsync`; `UpdateValueInBackground` → `ThreadPool.QueueUserWorkItem`.

**`BufferedECEClient`** (`BufferedECEClient.cs`) — Primary consumer. Creates `IHybridCache<ActionPlanInstance>` and `AutoCacheECEResult<CloudParametersXml>`. Subscribes to `ICacheInvalidator` events for invalidation.

#### 2.3 Configuration

**`CacheSettings`** — Resolved via `CacheSettings.GetCacheSettings<T>(configDict)`. Contains:
- `MemoryCacheSettings` (TTL, SizeLimit, CompactionPercentage, IsDisabled)
- `FileCacheSettings` (TTL, CacheFolder, PurgeExpirationInDays, IsDisabled)
- `BackgroundRefreshSettings` (IsEnabled, ValueFactoryTimeout default 30s, StalenessThreshold)
- `IsDisabled`, `IsDefault`, `ReportCacheStats`

**Config key convention:** `"{ParameterName}_{TypeName}"` via `CacheConstants.TypeSettingSuffix<T>()`. Example: `"MemoryCacheTimeToLive_ActionPlanInstance"`.

**Source:** Service Fabric `Settings.xml` → `ServiceConfigSection` parameters → `Dictionary<string,string>`.

### 3. DI Wiring Chain

```
1. services.AddSingleton<IMemoryCache>(new MemoryCache(options))
2. services.AddSingleton<IDistributedCache>(new CsvFileCache(fileCacheSettings, clock))
3. services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())
4. services.AddHybridCache()   // registers Microsoft HybridCache
5. var innerCache = provider.GetRequiredService<HybridCache>()
6. IHybridCacheFactory = new HybridCacheFactory(innerCache, memoryCache, distributedCache, configDict)
7. IHybridCache<T> = cacheFactory.CreateCache<T>(ttl, timeout)  // at consumer construction
```

**Critical:** `SerializerFactory` must be registered **before** `AddHybridCache()`. The Microsoft HybridCache discovers it from DI during construction.

### 4. Non-Obvious Wiring

#### 4.1 List Decomposition
Lists are **never stored as blobs**. `GetOrAddListAsync` decomposes a list into individual items, each stored under `itemBaseKey.WithHash(listCacheKey.Hash)`. The `CacheKeyMapping` maintains the bidirectional mapping. Reading a list re-assembles it by iterating `cacheKeyMapping.GetItemsInList()` and calling `GetFromCacheAsync` per item.

#### 4.2 Hash-Based Item Synchronization
`SynchronizeItemAcrossHashes` (line 579) ensures that when an item is updated, it propagates to all list contexts sharing the same hash. Items only sync with lists having matching hashes via `cacheKeyMapping.GetOrAddListsByHash<T>()`.

#### 4.3 Read-Only Options
`GetFromCacheAsync` and `ExistsAsync` use `readonlyCacheEntryOptions` with flags `DisableDistributedCacheWrite | DisableLocalCacheWrite`. This means they **read** from both tiers but **never write back**, preventing phantom entries from the no-op value factory.

#### 4.4 Factory Config Side Effects
`HybridCacheFactory.PopulateConfig<T>()` mutates the shared `configDict` when creating a cache. If `valueFactoryTimeout` is provided, it **removes** `StalenessThreshold`; if not, it **removes** `ValueFactoryTimeout` and sets `StalenessThreshold = 5min`. These are mutually exclusive modes.

#### 4.5 CsvFileCache Direct Casts
`DefaultHybridCache<T>` casts `IDistributedCache` to `CsvFileCache` in two places:
- `ClearAsync` (line 534) — calls `ClearAllFiles(forceDelete: true)` only on full purge (no prefix filter).
- `ReportStats` (line 773) — calls `GetStatistics()` for file-tier metrics.

### 5. Critical Invariants

1. **`T : ICachedItem`** — Every cached type must implement `ICachedItem` (provides `GetCacheKey()` and `GetLastModifiedTime()`).
2. **One instance per type** — `HybridCacheFactory.cacheInstances` uses `GetOrAdd(typeof(T), ...)`. A type cannot have multiple cache instances with different settings.
3. **Settings validation** — `DefaultHybridCache<T>` constructor validates `CacheSettings` via `DataAnnotations.Validator.TryValidateObject`. Invalid settings throw immediately.
4. **Value factory exceptions are captured, not thrown** — `GetOrAddAsync` returns `(result, valueFactoryException)` tuple. Callers must check the exception field.
5. **Null from SetAsync triggers RemoveAsync** — `SetAsync(key, null)` deletes the entry.
6. **ClearAsync with no prefixes calls ClearAllFiles** — This is the only code path that deletes orphaned L2 files.
7. **Tags are always `{ typeof(T).Name }`** — Set once in the constructor (line 91), never modified.

### 6. Adding New Functionality

**To cache a new type `MyType`:**
1. Implement `ICachedItem` on `MyType` (`GetCacheKey()`, `GetLastModifiedTime()`).
2. Ensure `SerializerFactory.TryCreateSerializer<MyType>()` returns a valid serializer.
3. At the consumer, call `cacheFactory.CreateCache<MyType>(ttl, timeout)`.
4. Optionally add type-specific config keys to `Settings.xml`: `MemoryCacheTimeToLive_MyType`, `FileCacheTimeToLive_MyType`, etc.

**To add a new cache operation:**
- Add to `IHybridCache<T>` / `IListHybridCache<T>` interface.
- Implement in `DefaultHybridCache<T>`.
- Update all three mock implementations (`MockHybridCache`, both `MockedHybridCache` variants).

**To add new invalidation events:**
- Add event to `ICacheInvalidator`.
- Subscribe in `BufferedECEClient` constructor (follow the pattern at lines 89–92).

### 7. Concurrency & Thread Safety

- **`HybridCacheFactory.cacheInstances`** — `ConcurrentDictionary<Type, IHybridCache>`; `GetOrAdd` ensures single creation per type.
- **`DefaultHybridCache<T>.cacheKeys`** — `ConcurrentDictionary<CacheKey, byte>`; safe for concurrent add/remove.
- **`CacheKeyMapping`** — implementation not provided, but must be thread-safe as it's accessed from main and background-refresh threads concurrently.
- **Background refresh** — `TriggerBackgroundRefresh` / `TriggerListBackgroundRefresh` use fire-and-forget `Task.Run` with a `CancellationTokenSource(ValueFactoryTimeout)`. No lock contention with the main path — they call `SetAsync` which goes through `HybridCache`'s internal synchronization.
- **`AutoCacheECEResult.UpdateValueInBackground`** — uses `ThreadPool.QueueUserWorkItem`, separate from the `Task.Run` path in `DefaultHybridCache<T>`.
- **No explicit locks** exist in `DefaultHybridCache<T>`. Thread safety relies on `ConcurrentDictionary`, Microsoft `HybridCache` internal locking, and the idempotent nature of cache writes.

### 8. Error Handling & Resilience

- **Value factory exceptions are swallowed:** `GetOrAddAsync` captures exceptions into `valueFactoryException` (line 197) and returns them in the tuple. The caller decides how to handle.
- **`OperationCanceledException` from value factory** triggers `TriggerBackgroundRefresh` (line 204) — converts cancellation into a deferred retry.
- **All non-cancellation exceptions in cache operations** are caught and wrapped in `CacheException` (lines 142, 224).
- **Background refresh failures** are caught and logged via `CacheEventLogger.CacheUnknownError` (lines 626, 652). They never propagate to the caller.
- **`isDisabled` flag** — When `CacheSettings.IsDisabled` is true, all cache operations short-circuit: reads return `default(T)`, `GetOrAddAsync` calls the value factory directly. This acts as a runtime circuit breaker.
- **Settings import validation** — `ImportSettings` validates before applying; invalid settings are rejected without corrupting the running cache.

### 9. Test Coverage & Known Gaps

**Well-tested areas:**
- `HybridCacheTests` — L1+L2 write-through, expiration, selective read (L1-only, L2-only), clear with/without prefixes, orphan file cleanup, list storage with hashes. Uses real `MemoryCache` + `CsvFileCache`.
- `HybridCacheNullTests` — Null/empty edge cases, background refresh with null/empty/different lists, stale item refresh, selective prefix clearing.
- `HybridCacheFactoryTests` — Factory creation, settings round-trip via `ExportSettings`.
- `ActionPlanInstanceCacheTest` — Full DI wiring with `BufferedECEClient`.

**Known gaps (inferred from analysis):**
- **`SerializerFactory` internals** — No tests for serialization edge cases (corrupt data, version mismatches, unknown types).
- **`CacheKeyMapping` thread safety** — No dedicated concurrency tests visible.
- **`ImportSettings` persistence** — XML write path tested only implicitly.
- **`TryGet(string)` by type name** — No test for runtime type-name lookup.
- **`RegisterHybridCache<T>`** — Manual registration path has no test coverage.
- **`BufferedECEClient` invalidation event handlers** — Complex flows (`MonitorUpdateActionPlanInstance` at 85 lines) tested only via integration.
- **Concurrent background refresh + main-thread write race** — No explicit race condition tests.
- **`CsvFileCache` failure modes** — Disk full, permission errors, corrupt files not tested through `DefaultHybridCache<T>`.