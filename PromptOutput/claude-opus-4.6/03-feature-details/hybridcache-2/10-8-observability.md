## 8. Observability

### 8.1 Logging — CacheEventLogger

**File**: `CacheEventLogger.cs` (132 lines)

| Method | Level | Structured Properties |
|--------|-------|----------------------|
| `CacheHit(key)` | Verbose | key, correlationId, filePath, memberName, lineNumber |
| `CacheMiss(key)` | Verbose | key, correlationId, filePath, memberName, lineNumber |
| `CacheGet(key)` | Verbose | key, correlationId, filePath, memberName, lineNumber |
| `CacheGetFailed(key, ex)` | — | key, ex.Message, correlationId |
| `CacheSet(key, mode)` | — | key, mode (add/update/remove), correlationId |
| `CacheSetFailed(key, ex)` | — | key, ex.Message, correlationId |
| `CacheRemove(key)` | — | key, correlationId |
| `CacheRemoveFailed(key, ex)` | — | key, ex.Message, correlationId |
| `CacheExpired(key)` | — | key, correlationId |
| `CacheCorrupted(key, errorMessage)` | — | key, errorMessage, correlationId |
| `CacheSettingError(errorMessage)` | — | errorMessage, correlationId |
| `CacheSerializerError(errorMessage)` | — | errorMessage, correlationId |
| `CacheUnknownError(key, errorMessage)` | — | key, errorMessage, correlationId |
| `ReportCacheSize(key, size)` | — | totalSize across all keys |
| `ReportCacheStats(stats)` | — | tier, entryCount, hits, misses |
| `HandleCacheInvalidation(key, type, details)` | — | key, invalidationType, details |

### 8.2 Metrics — ETW EventCounters

**File**: `CacheEventSource.cs`

ETW source name: `Microsoft-UpdateService-CacheEventSource`

| Counter | Type |
|---------|------|
| `cacheHitCounter` | `EventCounter` (OnCacheHit) |
| `cacheMissCounter` | `EventCounter` (OnCacheMiss) |
| `cacheGetCounter` | `EventCounter` (OnCacheGet) |
| `cacheSetCounter` | `EventCounter` (OnCacheSet) |
| `cacheRemoveCounter` | `EventCounter` (OnCacheRemove) |
| `cacheSizeCounter` | `EventCounter` (OnCacheSize) |

### 8.3 Telemetry in ResourceResolver

`UpdateServiceTelemetryEmitter.StartActivity()` records:
- `GetResourceResolverDuration` (line 147) — single item resolve
- `GetAllResourceResolverDuration` (line 265) — list resolve
- Properties: CacheKey, Status, CacheMiss, Count, Log, Error

### 8.4 Stats Reporting

`DefaultHybridCache<T>.EmitStats()` (line 889): Every 5 minutes, reports L1 (MemoryCache) and L2 (CsvFileCache) statistics via `CacheEventLogger.ReportCacheStats()`.

### 8.5 Gaps

- Background refresh `Task.Run` failures are logged (`CacheUnknownError`) but NOT tracked by any counter
- `CacheKeyMapping` operations have no observability
- `HybridCacheFactory.CreateCache/TryGet` operations not logged
- No health check for cache layer availability

---