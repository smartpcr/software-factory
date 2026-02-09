## 15. Storage & Persistence Layers

### 15.1 Architecture Overview

The system implements a **two-tier cache-aside with write-through** pattern orchestrated by Microsoft's `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`):

| Tier | Implementation | Storage | Interface |
|------|---------------|---------|-----------|
| **L1 (Local)** | `MemoryCache` | In-process memory | `IMemoryCache` |
| **L2 (Distributed)** | `CsvFileCache` | CSV files on disk | `IDistributedCache` |
| **Orchestrator** | `Microsoft.Extensions.Caching.Hybrid.HybridCache` | N/A | `HybridCache` |
| **Application Wrapper** | `DefaultHybridCache<T>` | In-memory key mappings | `IHybridCache<T>` |

### 15.2 L1 — In-Memory Cache (`MemoryCache`)

#### 15.2.1 Configuration & Construction

Constructed in DI setup (visible in every test `Initialize()` method):

- **HybridCacheTests.cs:40-41** — `new MemoryCache(new MemoryCacheOptions { Clock = this.clock })`
- **HybridCacheNullTests.cs:47-48** — identical pattern
- **ActionPlanInstanceCacheTest.cs:56-68** — full production-like configuration:
  - `Clock` = injectable `FakeTime` (tests) or system clock (production)
  - `SizeLimit` = from `globalSettings.MemoryCacheSettings.SizeLimit`
  - `TrackStatistics` = from `globalSettings.ReportCacheStats`
  - `TrackLinkedCacheEntries = false`
  - `CompactionPercentage` = from `globalSettings.MemoryCacheSettings.CompactionPercentage`
  - `ExpirationScanFrequency` = half of TTL, or `CacheConstants.DefaultMemoryCacheTimeToLive`

Registered as singleton: `services.AddSingleton<IMemoryCache>(this.memoryCache)` — **HybridCacheTests.cs:42**, **HybridCacheNullTests.cs:49**, **ActionPlanInstanceCacheTest.cs:76**

#### 15.2.2 Key Format

All entries are keyed by `CacheKey.FullKey` (a `string`). Microsoft's `HybridCache` internally wraps values as `MutableCacheItem<T>` before storing in `IMemoryCache` — confirmed by test comment at **HybridCacheTests.cs:89**: *"note: hybrid cache pushes internal wrapper MutableCacheItem<T> to memory cache"*.

#### 15.2.3 Write Path (L1)

**Path 1 — via `SetAsync`** (DefaultHybridCache.cs:172-206):
```
DefaultHybridCache<T>.SetAsync(key, value, expiration)
  → hybridCache.SetAsync(key.FullKey, value, cacheEntryOptions.WithTtl(expiration), tags)
    → [Microsoft HybridCache internals] → memoryCache.Set(key, MutableCacheItem<value>)
```

**Path 2 — via `GetOrAddAsync` on cache miss** (DefaultHybridCache.cs:131-163):
```
DefaultHybridCache<T>.GetOrAddAsync(key, valueFactory)
  → hybridCache.GetOrCreateAsync(key.FullKey, factory, cacheEntryOptions, tags)
    → [cache miss] → valueFactory(ct) → writes result to both L1 and L2
```

**Path 3 — via `CacheListItems`** (DefaultHybridCache.cs:339-371):
```
CacheListItems(listCacheKey, list, expiration)
  → foreach item in list:
      → hybridCache.SetAsync(itemCacheKey.FullKey, item, cacheEntryOptions.WithTtl(expiration), tags)
```

#### 15.2.4 Read Path (L1)

**Path 1 — `GetFromCacheAsync`** (DefaultHybridCache.cs:94-133):
```
GetFromCacheAsync(key)
  → hybridCache.GetOrCreateAsync(key.FullKey, noop_factory, readonlyCacheEntryOptions, tags)
    → [Microsoft HybridCache] checks L1 first → if hit, returns immediately
```

**Path 2 — `ExistsAsync`** (DefaultHybridCache.cs:208-227):
```
ExistsAsync(key)
  → hybridCache.GetOrCreateAsync<T>(key.FullKey, noop_factory, readonlyCacheEntryOptions)
    → returns result != null
```

Direct L1 access in tests: `memoryCache.Get(key.