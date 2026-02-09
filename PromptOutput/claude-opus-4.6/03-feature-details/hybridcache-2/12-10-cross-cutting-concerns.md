## 10. Cross-Cutting Concerns

### 10.1 Validation

- `CacheSettings` validated in `DefaultHybridCache<T>` ctor using `DataAnnotations.Validator.TryValidateObject()` (line 91–97)
- `CacheSettings.ImportSettings()` validates before applying (line 987–995)
- `ResourceResolver` validates `resolverTimeout >= MinimumResolverTimeout` (line 78)
- `CsvFileCache.CacheKeyToFilePath()` throws on null/empty key (line 414)

### 10.2 Authorization

The `CacheController` is an admin REST API endpoint. Authorization is handled at the controller/middleware level, outside the HybridCache feature itself. The cache feature has no internal authorization checks.

---