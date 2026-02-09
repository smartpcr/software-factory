## 7. Runtime Data Flow

### Read Path: `GetOrAddAsync`

```
Consumer calls IHybridCache<T>.GetOrAddAsync(key, valueFactory)
  └→ DefaultHybridCache<T>.GetOrAddAsync (line 163)
       ├→ [disabled?] call valueFactory directly, return
       └→ HybridCache.GetOrCreateAsync(key.FullKey, factory, cacheEntryOptions.WithTtl(...))
            ├→ [L1 hit] return from MemoryCache
            ├→ [L1 miss, L2 hit] deserialize from CsvFileCache via SerializerFactory, fill L1
            └→ [both miss] call factory lambda (line 185)
                 └→ valueFactory(ct) → result
                      ├→ [success] HybridCache stores in L1+L2
                      └→ [exception] captured in valueFactoryException, not thrown
       ├→ [result != null] cacheKeys.TryAdd, SynchronizeItemAcrossHashes
       ├→ [cache hit + stale] TriggerBackgroundRefresh
       └→ return (result, valueFactoryException)
```

### Read Path: `GetOrAddListAsync`

```
Consumer calls IHybridCache<T>.GetOrAddListAsync(valueFactory, listCacheKey?)
  └→ DefaultHybridCache<T>.GetOrAddListAsync (line 377)
       ├→ GetListFromCacheAsync(listCacheKey)
       │    └→ cacheKeyMapping.GetItemsInList(listCacheKey)
       │         └→ for each itemKey: GetFromCacheAsync(itemKey)
       │              └→ hybridCache.GetOrCreateAsync (read-only)
       ├→ [all items present] return cached list, maybe TriggerListBackgroundRefresh
       └→ [miss] valueFactory(token)
            ├→ [non-empty result] CacheListItems(listCacheKey, list)
            │    └→ for each item:
            │         ├→ itemCacheKey = item.GetCacheKey().WithHash(listCacheKey.Hash)
            │         ├→ hybridCache.SetAsync(itemCacheKey.FullKey, item, ...)
            │         └→ cacheKeys.TryAdd
            │    └→ cacheKeyMapping.UpdateListMappings(listCacheKey, allItemKeys)
            └→ [null/empty] RemoveListAndAssociatedItems(listCacheKey)
```

### Write Path: `SetAsync`

```
Consumer calls IHybridCache<T>.SetAsync(key, value)
  └→ DefaultHybridCache<T>.SetAsync (line 237)
       ├→ [value null] RemoveAsync(key) → hybridCache.RemoveAsync + clear mappings
       └→ [value non-null]
            ├→ hybridCache.SetAsync(key.FullKey, value, ...) → writes L1+L2
            ├→ cacheKeys.TryAdd(key)
            └→ SynchronizeItemAcrossHashes(key, value)
                 └→ cacheKeyMapping.GetOrAddListsByHash<T>(parentListKey)
                      └→ for each matching list: AddItemToListMapping
```

### Background Refresh Path

```
ShouldBackgroundRefresh(item) returns true
  └→ TriggerBackgroundRefresh (line 616)
       └→ Task.Run(async):
            ├→ value = await valueFactory(cts.Token)
            └→ await SetAsync(key, value, expiration, cts.Token)
                 └→ [full SetAsync path including SynchronizeItemAcrossHashes]
```

---