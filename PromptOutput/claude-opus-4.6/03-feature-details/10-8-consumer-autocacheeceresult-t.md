## 8. Consumer: `AutoCacheECEResult<T>`

**AutoCacheECEResult.cs** — Thin wrapper providing get/set/update semantics over `IHybridCache<T>`.

| Method | Cache Call | Line |
|---|---|---|
| `GetValueAsync(cacheKey)` | `cache.GetOrAddAsync(cacheKey, getMethod)` | 43-44 |
| `SetValueAsync(value)` | `cache.SetAsync(value.GetCacheKey(), value)` | 57 |
| `UpdateValueAsync(cacheKey)` | `getMethod()` → `cache.SetAsync(cacheKey, result)` | 69, 95-96 |
| `UpdateValueInBackground(cacheKey)` | `ThreadPool.QueueUserWorkItem` → `UpdateCachedValue` | 74-83 |

**Consumed by**: `BufferedECEClient.cloudParametersXML` field (BufferedECEClient.cs:38)

---