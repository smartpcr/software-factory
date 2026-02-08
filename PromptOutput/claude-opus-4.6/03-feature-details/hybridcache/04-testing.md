## Testing

### Unit Tests

| Test Class | Location | What It Covers | Mocking |
|------------|----------|----------------|---------|
| `HybridCacheTests` | `Common\UnitTest` | Core get/set, two-tier persistence, expiration, list management, clear by prefix | Real impls, FakeTime clock |
| `HybridCacheNullTests` | `Common\UnitTest` | Null value handling, empty list removal, background refresh with nulls | Real impls, FakeTime |
| `HybridCacheFactoryTests` | `Common\UnitTest` | Factory creation with TTL, settings export/regeneration | Real components |
| `CacheProviderTests` | `Common\UnitTest` | Memory/File/Registry/Hybrid providers, 5 payload sizes (0B–5MB) | Data-driven, real impls |
| `CacheExpirationTests` | `Common\UnitTest` | Sliding + absolute expiration policies, refresh behavior | Real serializers, clock control |
| `CacheInvalidationTests` | `Common\UnitTest` | Status-based caching (InProgress bypasses cache), concurrent updates | Real cache |
| `CacheConcurrencyTests` | `Common\UnitTest` | Thread-safety with 20 concurrent iterations, modification time consistency | SemaphoreSlim, Interlocked |
| `CacheStampedeTests` | `Common\UnitTest` | Stampede prevention (single factory invocation), cancellation handling | Semaphores for coordination |
| `CacheSettingTests` | `Common\UnitTest` | Settings export/import, configuration validation | Config dictionaries |
| `CacheSynchronizationTests` | `Common\UnitTest` | Item-list mapping synchronization, hash-based query isolation | `Mock<ICachedItem>` (Moq) |
| `CacheStatsReportTests` | `Common\UnitTest` | Hit/miss statistics (1000 items, 100 unique IDs), per-tier reporting | TrackStatistics enabled |
| `CacheErrorHandlerTests` | `Common\UnitTest` | Serialization errors → `CacheException`, factory exceptions, cancellation | **Moq** (`MockBehavior.Strict`) |
| `BackgroundRefreshTests` | `Common\UnitTest` | Staleness threshold, null item refresh, empty list triggers | Real impls |
| `DependencyInjectionTests` | `Common\UnitTest` | Unity container registration, service resolution | Real Unity container |
| `ValueFactoryTimeoutTests` | `Common\UnitTest` | Short timeout → background refresh with extended timeout | TaskCompletionSource |
| `CacheControllerTests` | `Controllers\UnitTest` | Controller methods, HTTP response validation | **Moq** strict mocks |
| `ResourceResolverListTests` | `ResourceCaching\UnitTest` | List resolution, cache hit/miss, null/empty handling | `MockedHybridCache` (custom) |
| `ResourceResolverGetTests` | `ResourceCaching\UnitTest` | Single resource resolution, timeout logic | `MockedHybridCache` (custom) |
| `DownloadConnectorTests` | `Core\UnitTest` | Runtime parameters, progress parsing | **Moq** strict mock of `HybridCache` |
| `HealthCheckHandlerTests` | `Core\UnitTest` | Report generation, missing health check files | **Moq** `IHybridCacheFactory` |
| `InstalledVersionStoreTests` | `Core\UnitTest` | Version loading/storing, BOM matching | `MockHybridCache` (pass-through) |
| `BufferedECEClientTests` | `EceClient\UnitTest` | Cache hit/miss, query hash differentiation, list updates | Real cache |
| `ActionPlanInstanceCacheTest` | `EceClient\UnitTest` | Single/list caching, TTL, query hash isolation | Real MemoryCache + CsvFileCache |
| `CloudParametersXmlCacheTests` | `EceClient\UnitTest` | 30-min TTL, miss→fetch, hit→no-fetch, expiry→refetch | Real cache |

**Mocking strategies:**
1. **Real implementations** with `FakeTime` clock injection (most common)
2. **Moq** with `MockBehavior.Strict` for serializer/factory isolation
3. **Custom mock classes**: `MockHybridCache` (pass-through), `MockedHybridCache` (configurable hit/miss)

### Integration Tests

| Test Class | Location | What It Tests |
|------------|----------|---------------|
| `CacheInvalidationTests` | `IntegrationTest` | Full-stack invalidation with mocked ECE backend, TTL expiry (10s) |
| `CacheControllerIntegrationTests` | `IntegrationTest` | REST API endpoints: GetCachedItems, ClearCachedItems, ExportSettings, ImportSettings, ReportStats |
| `EceClientCacheTests` | `IntegrationTest` | StampVersion + ActionPlanInstance caching, cache hit verification |
| `UpdateResourceIntegrationTests` | `IntegrationTest` | End-to-end update resource with cache |
| `UpdateLocationIntegrationTests` | `IntegrationTest` | Location resource resolution with cache |
| `UpdateRunIntegrationTests` | `IntegrationTest` | Run resource with cache |
| `OnlineDiscoveryTests` | `IntegrationTest` | Discovery with cache interaction |
| `ResourceProviderIntegrationTests` | `ResourceProvider\IntegrationTest` | Generic resource provider with `MockedHybridCache` |

**Infrastructure:** Unity DI container, `ConfigurationHandlerTestUtilities.SetupCacheSettings()` / `ClearCaches()`, real `MemoryCache` + `CsvFileCache`.

### Test Coverage Gaps

1. **`WindowsRegistryCache`** — no dedicated unit tests visible (only exercised via `CacheProviderTests` parameterized runs)
2. **`CacheInvalidator` event wiring** — individual `Raise*` event handlers are not tested in isolation
3. **`SerializerFactory` security checks** — BinaryFormatter restriction logic lacks explicit tests
4. **`CsvFileCache` concurrent file access** — no stress tests for per-file locking under contention
5. **`DefaultCacheKey.SanitizeFilename`** — no explicit edge case tests for long paths or Unicode
6. **`OsUpdate.cs`** cache integration — only passes factory through, no direct tests
7. **L2 → L1 promotion** — no explicit test verifying memory cache is populated after file cache hit
8. **Memory pressure / compaction** — `MemoryCacheSettings.CompactionPercentage` behavior untested
9. **`HybridCacheEntryFlags` conditional logic** — `DisableLocalCache`/`DisableDistributedCache` flag toggling path not explicitly tested end-to-end

---