## Implementation Across Layers

### Configuration / Startup

| File | Role |
|------|------|
| `src\UpdateResourceProvider\UpdateService\Service\Source\UnityConfig.cs` | Central DI wiring — `RegisterCaching()` method |
| `src\Common\Common\Source\Caching\HybridCacheOptionExtensions.cs` | `WithTtl()` extension on `HybridCacheEntryOptions` |
| `src\Common\Common\Source\Caching\CacheSettings.cs` | Type-specific settings parsed from config dictionary |

### API / Controllers / Endpoints

**`CacheControllerImplementation`** (`src\UpdateResourceProvider\UpdateService\Controllers\Source\CacheControllerImplementation.cs`):
- `ClearAsync(typeName, prefixes)` — clears cache by type and optional key prefix
- `ExportSettings(typeName)` — exports cache settings JSON
- `ImportSettings(typeName, settings, settingFile)` — imports cache settings
- `GetListFromCacheAsync(typeName, cacheKey)` — retrieves cached list
- `GetFromCacheAsync(typeName, cacheKey)` — retrieves single cached item
- `ReportStats(typeName)` — returns `List<CacheStatistics>` (hit/miss/size)

Supports types: `Update`, `UpdateRun`, `UpdateLocation`, `DiscoveryDiagnosticInfo`, `ActionPlanInstance`, `CloudParametersXml`

### Business Logic / Domain

| File | Class | What It Caches |
|------|-------|----------------|
| `UpdateResourceHandler.cs` | `UpdateResourceHandler` | `IHybridCache<Update>` via `ResourceResolver` |
| `UpdateLocationResourceHandler.cs` | `UpdateLocationResourceHandler` | `IHybridCache<UpdateLocation>` |
| `UpdateRunResourceHandler.cs` | `UpdateRunResourceHandler` | `IHybridCache<UpdateRun>` |
| `DiscoveryDiagnosticInfoHandler.cs` | `DiscoveryDiagnosticInfoHandler` | `IHybridCache<DiscoveryDiagnosticInfo>` |
| `HealthCheckHandler.cs` | `HealthCheckHandler` | `IHybridCache<ActionPlanInstance>` via factory |
| `InstalledVersionStore.cs` | `InstalledVersionStore` | `ConcurrentBag<InstalledVersion>` via raw `HybridCache` |
| `DownloadConnector.cs` | `DownloadConnector` | `bool` (download support flag) via raw `HybridCache` |
| `BufferedECEClient.cs` | `BufferedECEClient` | `IHybridCache<ActionPlanInstance>`, `AutoCacheECEResult<CloudParametersXml>` |
| `ResourceResolver.cs` | `ResourceResolver<T>` | Generic `IHybridCache<T>` for any `ICachedItem` resource |

**Domain interfaces:**
- `ICachedItem` — `GetCacheKey()`, `GetLastModifiedTime()`, `GetStateLogMessage()`
- `CacheKey` (struct) — `ForItem<T>()`, `ForList<T>()`, `WithHash()`, `Parse()`

### Persistence / Data Access

**`CsvFileCache`** (`src\Common\Common\Source\Caching\CsvFileCache.cs`):
- Implements `IDistributedCache` using cluster shared volume files
- Per-file locking (not global), atomic write via temp + rename
- TTL enforcement with optional purge of expired files
- Filename sanitization via `DefaultCacheKey.SanitizeFilename()`

**`WindowsRegistryCache`** (`src\Common\Common\Source\Caching\WindowsRegistryCache.cs`):
- Alternative `IDistributedCache` using Windows Registry
- Supports absolute and sliding expiration

### Serialization / DTOs

| Class | Backend | Interface |
|-------|---------|-----------|
| `HybridJsonSerializer<T>` | Newtonsoft.Json | `IHybridCacheSerializer<T>` |
| `HybridJsonTextSerializer<T>` | System.Text.Json | `IHybridCacheSerializer<T>` |
| `JsonTextSerializer<T>` | System.Text.Json | `IHybridCacheSerializer<T>` |
| `DefaultJsonSerializer<T>` | Newtonsoft.Json | `ICachedItemSerializer<T>` → `IHybridCacheSerializer<T>` |
| `DefaultTextSerializer<T>` | System.Text.Json | `ICachedItemSerializer<T>` → `IHybridCacheSerializer<T>` |
| `SerializerFactory` | — | `IHybridCacheSerializerFactory` (selects serializer per type) |

**Notable:** `DefaultJsonSerializer` and `DefaultTextSerializer` include workarounds for a HybridCache bug that flattens single-element `List<T>` to a bare object during serialization.

### Messaging / Events

**`CacheInvalidator`** (`src\Common\Common\Source\Caching\CacheInvalidator.cs`) — event-based invalidation:

| Event | Trigger |
|-------|---------|
| `OnUpdateLocationHealthCheckFinished` | Health check completes |
| `OnUpdateAdded` / `OnUpdateRefreshed` | Update created/refreshed |
| `OnUpdateRunAdded` / `OnUpdateRunRefreshed` | UpdateRun state changes |
| `OnRefreshActionPlanInstance` | Action plan completes |
| `OnRefreshCloudParametersXml` | Cloud params change |
| `OnRefreshDiscoveryConfig` | Discovery config refresh |
| `OnRefreshFeatureFlag` | Feature flag toggle |

All use `[CallerFilePath]`, `[CallerMemberName]`, `[CallerLineNumber]` for traceability.

### Background Processing

- **Background refresh** in `DefaultHybridCache<T>`:
  - **Regular refresh**: `ValueFactoryTimeout` triggers a second attempt with extended timeout
  - **Smart refresh**: `StalenessThreshold` (default 5 min) checks `GetLastModifiedTime()` on items; if stale, triggers background fetch
- **`AutoCacheECEResult<T>`** — `UpdateValueInBackground()` uses `ThreadPool.QueueUserWorkItem` for non-blocking refresh
- **`BufferedECEClient`** — monitors action plan instances, triggers background refresh via `ICacheInvalidator` events

### Cross-cutting Concerns

**Logging/Telemetry:**
- `CacheEventLogger` (`src\Common\Common\Source\Trace\CacheEventLogger.cs`) — static utility logging Hit/Miss/Get/Set/Remove/Refresh/Failed events via `CacheEventSource` (ETW)
- Uses correlation IDs for distributed tracing

**Stampede Prevention:**
- `DefaultHybridCache<T>.GetOrAddAsync()` coalesces concurrent requests for the same key — only one factory invocation, others wait

**Hash-based Query Isolation:**
- `DefaultCacheKey.GetConsistentHash()` — SHA256 hash of JSON-serialized query objects
- Prevents cross-contamination between different query results for same type

**Statistics:**
- `CacheStatistics` — tracks `TotalHits`, `TotalMisses`, `CurrentEntryCount`, `CurrentEstimatedSize` per `CacheTier` (Local, File, Registry)

---