## 4. Extension: `HybridCacheOptionExtensions`

**HybridCacheOptionExtensions.cs:14-27** — `WithTtl(this HybridCacheEntryOptions, TimeSpan?)`

- If `ttl` has value: creates new `HybridCacheEntryOptions` with:
  - `Expiration` = ttl (unless `DisableDistributedCache` flag set)
  - `LocalCacheExpiration` = ttl (unless `DisableLocalCache` flag set)
  - Preserves `Flags`
- If no ttl: returns original options unchanged
- **Called from**: DefaultHybridCache.cs — `GetOrAddAsync` (line 191), `SetAsync` (line 254), `CacheListItems` (line 568)

---