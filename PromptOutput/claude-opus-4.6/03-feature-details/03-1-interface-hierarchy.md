## 1. Interface Hierarchy

### Base Interface: `IHybridCache`
**IHybridCache.cs:17-23** — Non-generic base with settings/stats operations:
- `ReportStats()` → `List<CacheStatistics>`
- `ExportSettings()` → `Task<CacheSettings>`
- `ImportSettings(CacheSettings, string)` → `Task<bool>`

### Generic Interface: `IHybridCache<T>`
**IHybridCache.cs:25-42** — Extends both `IHybridCache` and `IListHybridCache<T>`, constrained to `T : ICachedItem`. Adds item-level operations:
- `GetFromCacheAsync(CacheKey, CancellationToken)`
- `GetOrAddAsync(CacheKey, Func<CancellationToken, Task<T>>, TimeSpan?, CancellationToken)`
- `SetAsync(CacheKey, T, TimeSpan?, CancellationToken)`
- `RemoveAsync(CacheKey, CancellationToken)`
- `ExistsAsync(CacheKey, CancellationToken)`
- `ClearAsync(string[], CancellationToken)`

### List Interface: `IListHybridCache<T>`
**IListHybridCache.cs:15-32** — List-specific operations, constrained to `T : ICachedItem`:
- `GetListFromCacheAsync(CacheKey?, CancellationToken)`
- `GetOrAddListAsync(Func<CancellationToken, Task<List<T>>>, CacheKey?, TimeSpan?, CancellationToken)`
- `SetListAsync(List<T>, CacheKey?, TimeSpan?, CancellationToken)`
- `ListExistsAsync(CacheKey?, CancellationToken)`

### Factory Interface: `IHybridCacheFactory`
**IHybridCacheFactory.cs:15-65** — Creates/retrieves typed cache instances:
- `CreateCache<T>(TimeSpan, TimeSpan?)` → `IHybridCache<T>`
- `CreateListCache<T>(TimeSpan, TimeSpan?)` → `IListHybridCache<T>`
- `RegisterHybridCache<T>(IHybridCache<T>)` — manual registration
- `TryGet<T>()` → `IHybridCache<T>` — by generic type
- `TryGet(string)` → `IHybridCache` — by type name string
- `GetFirstOrDefault()` → `IHybridCache` — any registered cache

---