## 10. Mock / Test Implementations

### `MockHybridCache` (UpdateService UnitTest)
**MockHybridCache.cs** — Extends Microsoft's `HybridCache` abstract class.
- `GetOrCreateAsync`: calls factory directly, no caching (line 19-24)
- `SetAsync` / `RemoveAsync`: throw `NotImplementedException`
- `RemoveByTagAsync`: no-op

### `MockedHybridCache` (ResourceProvider IntegrationTest)
**MockedHybridCache.cs** — Implements `IHybridCache<TestFabricResource>`.
- `GetOrAddListAsync` / `GetOrAddAsync`: call valueFactory directly (lines 30-40, 60-70)
- All other methods: throw `NotImplementedException`

### `MockedHybridCache` (ResourceCaching UnitTest)
**MockedHybridCache.cs** — Implements `IHybridCache<T>` (T = TestFabricResource).
- Stores pre-set `resource` / `resources` in constructor
- `GetListFromCacheAsync`: returns stored `resources` (line 74)
- `GetOrAddListAsync` / `GetOrAddAsync`: call valueFactory (lines 77-89, 99-109)
- Other mutation methods: return `Task.CompletedTask` or `false`

---