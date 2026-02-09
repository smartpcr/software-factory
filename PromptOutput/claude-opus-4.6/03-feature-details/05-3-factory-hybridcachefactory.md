## 3. Factory: `HybridCacheFactory`

**HybridCacheFactory.cs:17** — `public class HybridCacheFactory : IHybridCacheFactory`

### Fields (lines 20-23)

| Field | Type | Purpose |
|---|---|---|
| `innerCache` | `HybridCache` | Microsoft's HybridCache singleton |
| `memoryCache` | `IMemoryCache` | Shared L1 |
| `distributedCache` | `IDistributedCache` | Shared L2 |
| `configDict` | `Dictionary<string,string>` | Config source |
| `cacheInstances` | `ConcurrentDictionary<Type, IHybridCache>` | **Singleton-per-type registry** |

### `CreateCache<T>` (lines 34-44)
- `cacheInstances.GetOrAdd(typeof(T), ...)` — **one instance per type**
- Calls `PopulateConfig<T>(expirySpan, valueFactoryTimeout)` to inject type-specific config
- Creates `new DefaultHybridCache<T>(innerCache, memoryCache, distributedCache, CacheSettings.GetCacheSettings<T>(configDict))`

### `CreateListCache<T>` (lines 47-57)
- Identical to `CreateCache<T>` but cast to `IListHybridCache<T>`
- **Shares the same `cacheInstances` dictionary** — a type can't have separate item-only and list-only caches

### `RegisterHybridCache<T>` (lines 60-63)
- `cacheInstances.AddOrUpdate(typeof(T), cache, (_, _) => cache)` — allows manual override

### `TryGet<T>` (lines 66-69)
- `cacheInstances.TryGetValue(typeof(T), ...)` → cast to `IHybridCache<T>`

### `TryGet(string)` (lines 72-75)
- `cacheInstances.FirstOrDefault(p => p.Key.Name.Equals(typeName, ...)).Value`
- **Runtime lookup by type name** — used by cache controller endpoints

### `GetFirstOrDefault` (lines 78-81)
- Returns any registered cache instance — for global settings/stats

### `PopulateConfig<T>` (lines 83-102)
- Writes to `configDict`:
  - `CacheConstants.MemoryCacheTimeToLiveParameterName + TypeSettingSuffix<T>()` = expirySpan
  - `CacheConstants.FileCacheTimeToLiveParameterName + TypeSettingSuffix<T>()` = expirySpan
  - If `valueFactoryTimeout` provided: sets `BackgroundValueFactoryTimeoutParameterName`, removes staleness threshold
  - If not provided: removes timeout, sets `BackgroundRefreshStalenessThresholdParameterName` = 5 min
- These config entries are later consumed by `CacheSettings.GetCacheSettings<T>(configDict)`

---