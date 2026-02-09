## 4. Runtime Behavior & Data Flow

### 4.1 Entry Points

| Entry Point | Type | File | How Cache is Used |
|------------|------|------|-------------------|
| `ResourceResolver<T>.GetResource()` | API controller → resolver | `ResourceResolver.cs:124` | `cache.GetOrAddAsync()` |
| `ResourceResolver<T>.GetResourceList()` | API controller → resolver | `ResourceResolver.cs:263` | `cache.GetOrAddListAsync()` |
| `BufferedECEClient.GetActionPlanInstance()` | Internal ECE call | `BufferedECEClient.cs:104` | `actionPlanInstanceCache.GetOrAddAsync()` |
| `AutoCacheECEResult<T>.GetValueAsync()` | CloudParams retrieval | `AutoCacheECEResult.cs:48` | `cache.GetOrAddAsync()` |
| `HealthCheckHandler` ctor | Service startup | `HealthCheckHandler.cs:71` | `cacheFactory.CreateCache<ActionPlanInstance>()` |
| `CacheControllerImplementation.ClearCachedItems()` | REST API (admin) | `CacheControllerImplementation.cs:64` | `cache.ClearAsync()` |
| `CacheControllerImplementation.ExportSettings()` | REST API (admin) | `CacheControllerImplementation.cs` | `cache.ExportSettings()` |
| `CacheControllerImplementation.ImportSettings()` | REST API (admin) | `CacheControllerImplementation.cs` | `cache.ImportSettings()` |
| `CacheInvalidator.InvalidateCache<T>()` | Post-mutation hook | `CacheInvalidator.cs:52` | `cache.SetAsync()` |
| `InstalledVersionStore` | Version tracking | `InstalledVersionStore.cs:49` | Direct `HybridCache` usage |

### 4.2 Single Item Read Path (GetOrAddAsync)

**File**: `DefaultHybridCache.cs` lines 183–279

```
1. Check isDisabled → if true, call valueFactory directly, return
2. Call HybridCache.GetOrCreateAsync(key.FullKey, factory, options, tags)
   → L1 (MemoryCache) lookup
   → L1 miss → L2 (CsvFileCache.GetAsync) lookup
   → L2 miss → Execute valueFactory
3. If result != null:
   a. Track cacheKey in this.cacheKeys ConcurrentDictionary
   b. If cache miss: SynchronizeItemAcrossHashes() → update list mappings
4. If cache hit and ShouldBackgroundRefresh(result):
   → TriggerBackgroundRefresh() (fire-and-forget Task.Run)
5. Log hit/miss via CacheEventLogger
6. Return (result, valueFactoryException)  ← caller handles exception
```

### 4.3 List Read Path (GetOrAddListAsync)

**File**: `DefaultHybridCache.cs` lines 453–540

```
1. Check isDisabled → if true, call valueFactory directly, return
2. GetListFromCacheAsync(listCacheKey):
   a. Get item keys from CacheKeyMapping
   b. For each itemKey → GetFromCacheAsync(itemKey)
   c. Return list if all items present
3. If cached list present AND count matches mappings → cache hit
   a. If ShouldBackgroundRefresh(list) → TriggerListBackgroundRefresh()
4. Else → cache miss:
   a. Call valueFactory
   b. If list returned: CacheListItems() → store each item individually, update mappings
   c. If empty/null: RemoveListAndAssociatedItems()
5. Return (list, valueFactoryException)
```

### 4.4 Cache Invalidation Flow

**File**: `CacheInvalidator.cs`

Events are raised by domain logic (e.g., after an update is added) and consumed by event handlers:

```
Domain code (UpdateResourceHandler) 
  → cacheInvalidator.RaiseUpdateAdded()
    → OnUpdateAdded event fired
      → Subscriber handles: calls cacheInvalidator.InvalidateCache<T>(value, getCacheKey)
        → cacheFactory.TryGet<T>() → cache.SetAsync(key, value)
```

---