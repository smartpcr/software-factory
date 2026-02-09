## 5. Resilience & Failure Handling

### 5.1 Failover / Graceful Degradation

**`isDisabled` flag** (`DefaultHybridCache.cs` line 58): When all cache layers are disabled, `GetOrAddAsync` falls through to calling `valueFactory` directly (lines 194–207). Cache is completely bypassed.

**Value factory exceptions are returned, not thrown** (`DefaultHybridCache.cs` lines 220–229): The `valueFactoryException` is captured in a local variable and returned in the tuple `(T, Exception)`. The caller decides how to handle it. This prevents HybridCache from caching failed results.

**Background refresh on timeout** (line 224–227): If `valueFactory` throws `OperationCanceledException` (timeout), `TriggerBackgroundRefresh()` is called to retry asynchronously while returning stale cached data.

### 5.2 Retry & Backoff

**CsvFileCache.ReadFileWithRetryAsync** (line 489):
- 3 retries, 100ms delay between attempts
- Handles transient file I/O errors

**CsvFileCache.EnsureFileDeletedAsync** (line 362):
- 10 retries, 100ms delay
- Polls until file is confirmed deleted

**ResourceResolver** (line 159): Uses `CancellationTokenSource(this.resolverTimeout)` linked with caller's token. Timeout defaults to `MinimumResolverTimeout`.

### 5.3 Error Handling

**`CacheException`** (`CacheException.cs`): Wraps all cache infrastructure errors. All catch blocks in `DefaultHybridCache` filter `OperationCanceledException` (re-thrown) and wrap everything else in `CacheException`.

**`ResourceResolver`** (lines 217–230): Catches `CacheException` and wraps in `ResourceOperationException`. Unknown exceptions are logged via `CacheEventLogger.CacheUnknownError()`.

### 5.4 Timeout Handling

**Background refresh timeout** (`DefaultHybridCache.cs` line 768):
```csharp
var timeout = this.cacheSettings.BackgroundRefreshSettings.ValueFactoryTimeout ?? TimeSpan.FromSeconds(30);
var cts = new CancellationTokenSource(timeout);
```

**ResourceResolver timeout** (`ResourceResolver.cs` line 159):
```csharp
using var timeoutCts = new CancellationTokenSource(this.resolverTimeout);
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeoutCts.Token);
```

---