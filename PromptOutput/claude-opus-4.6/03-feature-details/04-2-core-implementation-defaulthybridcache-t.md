## 2. Core Implementation: `DefaultHybridCache<T>`

**DefaultHybridCache.cs:33** — `public class DefaultHybridCache<T> : IHybridCache<T> where T : ICachedItem`

### Fields (lines 37-55)

| Field | Type | Purpose |
|---|---|---|
| `hybridCache` | `HybridCache` (Microsoft) | Inner L1+L2 cache engine |
| `memoryCache` | `IMemoryCache` | L1 direct access for stats |
| `distributedCache` | `IDistributedCache` | L2 direct access for stats/clear |
| `tags` | `List<string>` | Set to `{ typeof(T).Name }` at line 91 |
| `cacheKeyMapping` | `ICacheKeyMapping` | List↔item relationship tracker |
| `defaultListCacheKey` | `CacheKey` | `CacheKey.ForList<T>(null, null)` at line 93 |
| `cacheKeys` | `ConcurrentDictionary<CacheKey, byte>` | All known cache keys for clear/enumerate |
| `cacheSettings` | `CacheSettings` | Type-specific settings |
| `isDisabled` | `bool` | Derived from `cacheSettings.IsDisabled` |
| `cacheEntryOptions` | `HybridCacheEntryOptions` | Write-enabled entry options |
| `readonlyCacheEntryOptions` | `HybridCacheEntryOptions` | Read-only (no write-back) |
| `lastCacheStatsReportTime` | `DateTime?` | Throttle stats emission |
| `cacheStatsReportInterval` | `TimeSpan` | 5 minutes |

### Constructor (lines 65-97)

1. Null-checks `cache` and `cacheSettings`
2. **Validates** `CacheSettings` via `System.ComponentModel.DataAnnotations.Validator.TryValidateObject` (lines 79-84)
3. Calls `this.RefreshCacheSettings()` (line 87) — builds `cacheEntryOptions` and `readonlyCacheEntryOptions`
4. Sets `this.tags = { typeof(T).Name }` (line 91)
5. Creates `this.defaultListCacheKey = CacheKey.ForList<T>(null, null)` (line 93)
6. Seeds `this.cacheKeys` with the default list key (line 94)
7. Creates `this.cacheKeyMapping = new CacheKeyMapping(this.defaultListCacheKey)` (line 95)

### Item Operations

**`GetFromCacheAsync`** (lines 108-150):
- If `isDisabled`, returns `default(T)`
- Calls `hybridCache.GetOrCreateAsync(key.FullKey, ..., readonlyCacheEntryOptions, tags)` — the value factory returns `default(T)`, so no write-through
- Catches non-cancellation exceptions → wraps in `CacheException`
- Logs hit/miss via `CacheEventLogger`
- Calls `EmitStats()`

**`GetOrAddAsync`** (lines 163-230):
- If `isDisabled`, calls `valueFactory` directly, returns result + exception
- Calls `hybridCache.GetOrCreateAsync(key.FullKey, ..., cacheEntryOptions.WithTtl(expiration), tags)`
- Inside the factory lambda:
  - Sets `cacheHit = false`
  - Calls `valueFactory(ct)`, catches exceptions into `valueFactoryException`
  - On `OperationCanceledException`, triggers `TriggerBackgroundRefresh`
- On non-null result: `cacheKeys.TryAdd(key, 0)`, if cache miss calls `SynchronizeItemAcrossHashes`
- On cache hit + stale: calls `TriggerBackgroundRefresh`
- Logs hit/miss, emits stats
- Returns `(result, valueFactoryException)` — **never throws from value factory**

**`SetAsync`** (lines 237-278):
- If value is null → calls `RemoveAsync`
- Otherwise → `hybridCache.SetAsync(key.FullKey, value, cacheEntryOptions.WithTtl(expiration), tags)`
- Tracks key in `cacheKeys`, calls `SynchronizeItemAcrossHashes`
- Logs "add"/"update"/"remove" mode

**`ExistsAsync`** (lines 280-296):
- Uses `hybridCache.GetOrCreateAsync` with `readonlyCacheEntryOptions` (no write-back)
- Returns `result != null`

**`RemoveAsync`** (lines 298-320):
- `hybridCache.RemoveAsync(key.FullKey)`
- `cacheKeys.TryRemove(key)`
- Clears mappings: `cacheKeyMapping.ClearListMappings(key)` if list, `ClearItemMappings(key)` if item

### List Operations

**`GetListFromCacheAsync`** (lines 332-360):
- Gets item keys from `cacheKeyMapping.GetItemsInList(listCacheKey)`
- Iterates and calls `GetFromCacheAsync` per item
- Returns assembled list (or null if empty)
- **Never stores the list blob directly** — only stores individual items

**`GetOrAddListAsync`** (lines 377-439):
- Checks cached list via `GetListFromCacheAsync` + validates all items present
- Cache hit path: returns cached list, optionally triggers `TriggerListBackgroundRefresh`
- Cache miss path: calls `valueFactory`, on success calls `CacheListItems`, on failure calls `RemoveListAndAssociatedItems`

**`SetListAsync`** (lines 449-483):
- If null/empty list → `RemoveListAndAssociatedItems`
- Otherwise → `CacheListItems`

**`ListExistsAsync`** (lines 485-496):
- Checks `cacheKeyMapping.GetItemsInList(listCacheKey).Any()`

### Clear (lines 507-547)

- Collects all `cacheKeys.Keys`
- If `cacheKeyPrefixes` provided, filters by prefix match (case-insensitive `IndexOf`)
- For each key: `hybridCache.RemoveAsync`, `cacheKeys.TryRemove`, clear mappings
- If **no prefixes** (purge all): casts `distributedCache` to `CsvFileCache` and calls `ClearAllFiles(forceDelete: true)` — **this is the only place CsvFileCache.ClearAllFiles is called**

### List-Item Sync Helpers

**`CacheListItems`** (lines 553-577):
- For each item in list:
  - Gets `itemBaseKey` from `item.GetCacheKey()`
  - Creates `itemCacheKey = itemBaseKey.WithHash(listCacheKey.Hash)` — hashes items to the list's hash
  - Calls `hybridCache.SetAsync(itemCacheKey.FullKey, item, ...)`
  - Tracks in `cacheKeys`
- After all items stored: `cacheKeyMapping.UpdateListMappings(listCacheKey, itemCacheKeys)`

**`SynchronizeItemAcrossHashes`** (lines 579-597):
- Gets `listCacheKey = itemCacheKey.GetParentListCacheKey<T>()`
- Gets matching lists via `cacheKeyMapping.GetOrAddListsByHash<T>(listCacheKey)`
- For each matching list: `cacheKeyMapping.AddItemToListMapping(itemCacheKey, listKey)`
- Items with hash sync **only** with lists having the **same** hash

**`RemoveListAndAssociatedItems`** (lines 599-612):
- Gets all item keys from `cacheKeyMapping.GetItemsInList(listCacheKey)`
- Removes each item from `hybridCache`, `cacheKeys`, and `cacheKeyMapping`
- Clears list mappings, removes list from `cacheKeys`

### Background Refresh

**`TriggerBackgroundRefresh`** (lines 616-632):
- Creates `CancellationTokenSource` with `BackgroundRefreshSettings.ValueFactoryTimeout` (default 30s)
- Fire-and-forget `Task.Run`: calls `valueFactory` then `SetAsync`
- Catches exceptions → `CacheEventLogger.CacheUnknownError`

**`TriggerListBackgroundRefresh`** (lines 634-658):
- Same pattern but calls `CacheListItems` or `RemoveListAndAssociatedItems`

**`ShouldBackgroundRefresh(T)`** (lines 695-710):
- Checks `BackgroundRefreshSettings.IsEnabled`
- If item is null → refresh
- Otherwise compares `(UtcNow - item.GetLastModifiedTime()) > StalenessThreshold`

**`ShouldBackgroundRefresh(List<T>)`** (lines 712-726):
- Same but uses `Min(item.GetLastModifiedTime())` across the list

### Settings Management

**`RefreshCacheSettings`** (lines 660-690):
- Sets `isDisabled` from `cacheSettings.IsDisabled`
- Builds `cacheEntryOptions`:
  - `Expiration` = `FileCacheSettings.TimeToLive` (unless file cache disabled)
  - `LocalCacheExpiration` = `MemoryCacheSettings.TimeToLive` (unless memory cache disabled)
  - `Flags` = combination of `DisableLocalCache` / `DisableDistributedCache`
- Builds `readonlyCacheEntryOptions` with `DisableDistributedCacheWrite | DisableLocalCacheWrite`

**`EmitStats`** (lines 733-744):
- Throttled to `cacheStatsReportInterval` (5 min)
- Calls `ReportStats()` → `CacheEventLogger.ReportCacheStats`

**`ReportStats`** (lines 746-784):
- Casts `memoryCache` to `MemoryCache` → `GetCurrentStatistics()` → `CacheStatistics` with `Tier=Local`
- Casts `distributedCache` to `CsvFileCache` → `GetStatistics()` → `CacheStatistics` with `Tier=File`

**`ExportSettings`** (lines 786-826):
- Reads `Settings.xml` from CWD, parses `ServiceConfigSection` parameters
- Calls `cacheSettings.AddToConfig<T>(configDict)`
- Returns `CacheSettings.GetCacheSettings<T>(configDict)`

**`ImportSettings`** (lines 828-850):
- Validates new settings
- Replaces `this.cacheSettings`, calls `RefreshCacheSettings()`
- Persists to XML via `settings.SetCacheSettingsToXml<T>(settingFile)`

---