## 12. Cross-Cutting Dependencies

| Type | Role | Referenced From |
|---|---|---|
| `ICachedItem` | Constraint interface (`GetCacheKey()`, `GetLastModifiedTime()`) | DefaultHybridCache.cs:33, all generic constraints |
| `CacheKey` | Struct with `FullKey`, `IsList`, `Hash`, `ForItem<T>()`, `ForList<T>()`, `WithHash()`, `GetParentListCacheKey<T>()` | Throughout DefaultHybridCache.cs |
| `DefaultCacheKey` | Has `GetConsistentHash()` | HybridCacheTests.cs:224 |
| `ICacheKeyMapping` / `CacheKeyMapping` | List↔item bidirectional mapping | DefaultHybridCache.cs:42, 95, 339, 556-597 |
| `CsvFileCache` | `IDistributedCache` impl, file-backed L2, has `Exists()`, `ClearAllFiles()`, `GetStatistics()` | DefaultHybridCache.cs:534, 773 |
| `SerializerFactory` | `IHybridCacheSerializerFactory` impl for JSON serialization | All test Initialize methods |
| `CacheSettings` | Config model with validation, static factory methods | DefaultHybridCache.cs:74-87, HybridCacheFactory.cs:40 |
| `CacheConstants` | Parameter name constants, `TypeSettingSuffix<T>()`, `DefaultMemoryCacheTimeToLive` | HybridCacheFactory.cs:85-97 |
| `CacheEventLogger` | Static ETW/trace logging for cache operations | DefaultHybridCache.cs throughout |
| `CacheException` | Custom exception wrapping cache failures | DefaultHybridCache.cs:142, 224, etc. |
| `CacheStatistics` | Stats DTO with `Tier`, `CurrentEntryCount`, `CurrentEstimatedSize`, `TotalHits`, `TotalMisses` | DefaultHybridCache.cs:758-780 |
| `CacheTier` | Enum: `Local`, `File` | DefaultHybridCache.cs:762, 773 |
| `CacheAction` | Enum: `NoOp`, etc. | MockedHybridCache.cs (UnitTest) |
| `ICacheInvalidator` / `CacheInvalidator` | Event-driven invalidation (`InvalidateCache`, `RaiseRefreshUpdateRun`) | BufferedECEClient.cs:36, 89-92 |
| `BufferedECEClientSettings` | Default TTLs for ActionPlanInstance and CloudDefinition | BufferedECEClient.cs:57 |
| `ActionPlanInstance` | Implements `ICachedItem`, has `GetItemCacheKey()`, `GetListCacheKey()`, `IsDifferent()`, `IsCompleted()` | BufferedECEClient.cs throughout |
| `CloudParametersXml` | Implements `ICachedItem`, has `GetSingleItemCacheKey()` | BufferedECEClient.cs:79-88 |
| `Customer` / `Product` | Test `ICachedItem` implementations | Test files |

---