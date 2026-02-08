## Dependency Injection & Registration

### Core DI Registration Chain

The primary registration happens in **`UnityConfig.cs`** (`src\UpdateResourceProvider\UpdateService\Service\Source\UnityConfig.cs`) within the `RegisterCaching()` method:

```
Program/Startup → UnityConfig.RegisterServiceComponents() → RegisterCaching()
```

**Step 1 — Configuration dictionary loaded:**
```csharp
var configDict = ConfigurationHandler.Instance.GetDictionary();
container.RegisterInstance<Dictionary<string, string>>("config", configDict);
```

**Step 2 — Microsoft.Extensions.Caching services:**
```csharp
services.AddSingleton<ISystemClock>(clock);
services.AddSingleton<IMemoryCache>(memoryCache);               // L1 - in-process
services.AddSingleton<IDistributedCache>(fileCache);             // L2 - CsvFileCache on CSV
services.AddSingleton<IHybridCacheSerializerFactory>(serializerFactory);
services.AddHybridCache(options =>
    options.DefaultEntryOptions = new HybridC
        Expiration = FileCacheSettings.TimeToLive,
        LocalCacheExpiration = MemoryCacheSettings.TimeToLive,
        Flags = HybridCacheEntryFlags.None  // conditionally DisableLocalCache/DisableDistributed

**Step 3 — Unity container final registrations:**
```csharp
container.RegisterInstance<IHybridCacheSerializerFactory>(serializerFactory);  // Singleton
container.RegisterInstance<IMemoryCache>(memoryCache);                         // Singleton
container.RegisterInstance<IDistributedCache>(fileCache);                      // Singleton
container.RegisterInstance<HybridCache>(hybridCache);                          // Singleton (MS inner cache)
container.RegisterInstance<IHybridCacheFactory>(hybridCacheFactory);           // Singleton
container.RegisterInstance<ICacheInvalidator>(cacheInvalidator);               // Singleton
```

### Factory Classes

| Factory | Interface | Creates |
|---------|-----------|---------|
| `HybridCacheFactory` | `IHybridCacheFactory` | `DefaultHybridCache<T>` per type (singleton dictionary) |
| `SerializerFactory` | `IHybridCacheSerializerFactory` | `IHybridCacheSerializer<T>` (chooses JSON strategy per type) |

**`HybridCacheFactory`** maintains a `ConcurrentDictionary<Type, IHybridCache>` for lazy singleton creation:
- `CreateCache<T>(TimeSpan expirySpan, TimeSpan? valueFactoryTimeout)` → `DefaultHybridCache<T>`
- `CreateListCache<T>(...)` → same type, returned as `IListHybridCache<T>`
- `RegisterHybridCache<T>(cache)` → register externally-created caches
- `TryGet<T>()` / `TryGet(string typeName)` → retrieve by type

### Configuration Settings Classes

| Class | Location | Key Properties |
|-------|----------|----------------|
| `CacheSettings` | `Source\Caching\CacheSettings.cs` | `MemoryCacheSettings`, `FileCacheSettings`, `BackgroundRefreshSettings`, `MaximumPayloadBytes` (100 MB), `IsDisabled` |
| `MemoryCacheSettings` | `Source\Caching\MemoryCacheSettings.cs` | `IsDisabled`, `CompactionPercentage` (5%), `SizeLimit` (100 MB), `TimeToLive` |
| `FileCacheSettings` | `Source\Caching\FileCacheSettings.cs` | `IsDisabled`, `CacheFolder`, `TimeToLive`, `PurgeExpirationInDays` |
| `BackgroundRefreshSettings` | `Source\Caching\BackgroundRefreshSettings.cs` | `IsEnabled` (true), `ValueFactoryTimeout` (3 min), `StalenessThreshold` (5 min) |

Settings are populated from a `Dictionary<string, string>` config via `CacheSettings.GetCacheSettings<T>(configDict)` — each cached type `T` has its own settings keyed by type name.

**Per-type TTL configuration in `UnityConfig`:**

| Cached Type | Memory TTL | File TTL | Stale Threshold |
|-------------|-----------|----------|-----------------|
| `Update` | 30 min | 30 min | 10 sec |
| `UpdateRun` | 30 min | 30 min | 10 sec |
| `UpdateLocation` | 30 min | 30 min | 10 sec |
| `DiscoveryDiagnosticInfo` | 30 min | 30 min | 10 sec |
| `ActionPlanInstance` | configurable | configurable | configurable |
| `CloudParametersXml` | configurable | configurable | — |

---