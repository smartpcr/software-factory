## 11. Testing

### 11.1 Core Unit Tests

| Test Class | File | Focus |
|-----------|------|-------|
| `HybridCacheTests` | `Common\Common\UnitTest\HybridCacheTests.cs` | 52 references — core Get/Set/Remove/List operations |
| `HybridCacheNullTests` | `Common\Common\UnitTest\HybridCacheNullTests.cs` | 52 references — null handling paths |
| `HybridCacheFactoryTests` | `Common\Common\UnitTest\HybridCacheFactoryTests.cs` | Factory CreateCache/TryGet |
| `CacheProviderTests` | `Common\Common\UnitTest\CacheProviderTests.cs` | Provider-level tests |
| `CacheSettingTests` | `Common\Common\UnitTest\CacheSettingTests.cs` | Settings parsing and validation |
| `CacheStatsReportTests` | `Common\Common\UnitTest\CacheStatsReportTests.cs` | Stats emission |
| `CacheConcurrencyTests` | `Common\Common\UnitTest\CacheConcurrencyTests.cs` | Multi-threaded access |
| `CacheStampedeTests` | `Common\Common\UnitTest\CacheStampedeTests.cs` | Stampede protection |
| `CacheErrorHandlerTests` | `Common\Common\UnitTest\CacheErrorHandlerTests.cs` | Error/exception paths |
| `CacheSynchronizationTests` | `Common\Common\UnitTest\CacheSynchronizationTests.cs` | Item-list sync |
| `CacheExpirationTests` | `Common\Common\UnitTest\CacheExpirationTests.cs` | TTL and expiration |
| `CacheInvalidationTests` | `Common\Common\UnitTest\CacheInvalidationTests.cs` | Cache invalidation |
| `CacheKeyMappingTests` | `Common\Common\UnitTest\CacheKeyMappingTests.cs` | Key mapping logic |
| `CacheKeyTests` | `Common\Common\UnitTest\CacheKeyTests.cs` | CacheKey struct |
| `CsvFileCacheTests` | `Common\Common\UnitTest\CsvFileCacheTests.cs` | L2 file cache |
| `CsvFileConcurrencyTests` | `Common\Common\UnitTest\CsvFileConcurrencyTests.cs` | L2 concurrent access |
| `FileCacheTests` | `Common\Common\UnitTest\FileCacheTests.cs` | File I/O |
| `SerializationTests` | `Common\Common\UnitTest\SerializationTests.cs` | All serializer roundtrips |
| `ValueFactoryTimeoutTests` | `Common\Common\UnitTest\ValueFactoryTimeoutTests.cs` | Timeout behavior |
| `BackgroundRefreshTests` | `Common\Common\UnitTest\BackgroundRefreshTests.cs` | Background refresh |
| `DependencyInjectionTests` | `Common\Common\UnitTest\DependencyInjectionTests.cs` | DI wiring |
| `CacheProviderBenchmark` | `Common\Common\Benchmark\CacheProviderBenchmark.cs` | Performance benchmarks |

### 11.2 Consumer Unit Tests

| Test Class | File |
|-----------|------|
| `BufferedECEClientTests` | `EceClient\UnitTest\BufferedECEClientTests.cs` |
| `ActionPlanInstanceCacheTest` | `EceClient\UnitTest\ActionPlanInstanceCacheTest.cs` |
| `CloudParametersXmlCacheTests` | `EceClient\UnitTest\CloudParametersXmlCacheTests.cs` |
| `CacheControllerTests` | `UpdateService\Controllers\UnitTest\CacheControllerTests.cs` |
| `HealthCheckHandlerTests` | `UpdateService\Core\UnitTest\HealthCheckHandlerTests.cs` |
| `InstalledVersionStoreTests` | `UpdateService\Core\UnitTest\InstalledVersionStoreTests.cs` |
| `DownloadConnectorTests` | `UpdateService\Core\UnitTest\DownloadConnectorTests.cs` |
| `UpdateModelSerializationTests` | `UpdateService\Models\UnitTest\UpdateModelSerializationTests.cs` |
| `UpdateModelCacheSynchronizationTests` | `UpdateService\Models\UnitTest\UpdateModelCacheSynchronizationTests.cs` |
| `ResourceResolverGetTests` | `ResourceCaching\UnitTest\Resolver\ResourceResolverGetTests.cs` |
| `ResourceResolverListTests` | `ResourceCaching\UnitTest\Resolver\ResourceResolverListTests.cs` |

### 11.3 Integration Tests

| Test Class | File |
|-----------|------|
| `CacheControllerIntegrationTests` | `IntegrationTest\Source\CacheControllerIntegrationTests.cs` |
| `CacheInvalidationTests` | `IntegrationTest\Source\CacheInvalidationTests.cs` |
| `EceClientCacheTests` | `IntegrationTest\Source\EceClientCacheTests.cs` |
| `UpdateResourceIntegrationTests` | `IntegrationTest\Source\UpdateResourceIntegrationTests.cs` |
| `UpdateRunIntegrationTests` | `IntegrationTest\Source\UpdateRunIntegrationTests.cs` |
| `UpdateLocationIntegrationTests` | `IntegrationTest\Source\UpdateLocationIntegrationTests.cs` |
| `ResourceProviderIntegrationTests` | `ResourceProvider\IntegrationTest\ResourceProviderIntegrationTests.cs` |

### 11.4 Mock Implementations

| Mock Class | File |
|-----------|------|
| `MockedHybridCache` | `ResourceCaching\UnitTest\Resolver\MockedHybridCache.cs` |
| `MockedHybridCache` | `ResourceProvider\IntegrationTest\MockedHybridCache.cs` |
| `MockHybridCache` | `UpdateService\Core\UnitTest\Caching\MockHybridCache.cs` |

### 11.5 Test Gaps

- **`CacheKeyMapping.GetOrAddListsByHash<T>()`** — hash-based list synchronization logic has no dedicated test file
- **`CsvFileCache` multi-node concurrent access** — no test simulates cluster shared volume contention across processes
- **`DefaultHybridCache.ShouldBackgroundRefresh()`** — internal method, testable but no explicit test found for staleness edge cases
- **`SerializerFactory.GetItemType()` complex reflection logic** — partially tested via serialization roundtrips but generic collection edge cases may be uncovered

---