## 11. Test Coverage Map

### `HybridCacheTests.cs`
Tests `DefaultHybridCache<Customer>` with real `HybridCache` + `CsvFileCache` + `MemoryCache`:

| Test | Exercises |
|---|---|
| `StoreCacheToBothLayers` | `GetOrAddAsync` → verifies L1 (MemoryCache.Get) and L2 (fileCache.GetAsync) |
| `AbsoluteExpiry` | `GetOrAddAsync` with TTL → clock advance → `ExistsAsync` returns false |
| `MemoryCacheMissFallbackToFile` | `hybridCache.SetAsync` with `DisableLocalCacheWrite` → `ExistsAsync` still true |
| `MemoryCacheHitSkipsFileRead` | `hybridCache.GetOrCreateAsync` with `DisableDistributedCacheWrite` → file not written |
| `ClearCachedItems` | `GetOrAddAsync` 100 items → `ClearAsync()` → all gone from both layers |
| `SetItemInCache` | `SetAsync` → `ExistsAsync`, `GetListFromCacheAsync` returns item |
| `GetOrCreateListInCache` | `GetOrAddListAsync` with custom key → `ListExistsAsync` |
| `SetListInCache` | `SetListAsync` with custom key → items stored with hash |
| `ClearAsync_WithNullPrefix_*` | `ClearAsync(null)` → `CsvFileCache.ClearAllFiles` deletes orphans |
| `ClearAsync_WithEmptyPrefix_*` | `ClearAsync([])` → same behavior |
| `ClearAsync_WithPrefix_*` | `ClearAsync(["SomePrefix"])` → orphan files survive |

### `HybridCacheNullTests.cs`
Tests null/empty edge cases with background refresh:

| Test | Exercises |
|---|---|
| `GetOrAddAsync_ReturnsNull_When*` | null from factory, missing key, exception |
| `SetAsync_WithNullValue_*` | null → remove behavior |
| `GetListFromCacheAsync_ReturnsNull_*` | empty cache |
| `GetOrAddListAsync_ReturnsNull_*` | null factory |
| `SetListAsync_WithNullList_*` / `WithEmptyList_*` | removal paths |
| `GetOrAddAsync_WithExistingItem_ValueFactoryReturnsNull_*` | cached item survives null factory |
| `GetOrAddListAsync_WithExistingList_ValueFactoryReturnsNull_*` | background refresh with null → removal |
| `GetOrAddListAsync_WithExistingList_ValueFactoryReturnsEmptyList_*` | background thread removes list |
| `GetOrAddListAsync_WithExistingList_ValueFactoryReturnsDifferentList_*` | background updates cache |
| `ClearAsync_WithCacheKeyPrefixes_*` | selective prefix clearing |
| `ClearAsync_WithoutPrefixes_*` | full clear with list+item count |

### `HybridCacheFactoryTests.cs`
Tests `HybridCacheFactory`:

| Test | Exercises |
|---|---|
| `CreateCacheWithTtl` | `CreateCache<Product>(5min)` → `ExportSettings` roundtrips correctly |

### `ActionPlanInstanceCacheTest.cs`
Tests `BufferedECEClient` integration with real cache infrastructure — setup shows full DI wiring chain.

---