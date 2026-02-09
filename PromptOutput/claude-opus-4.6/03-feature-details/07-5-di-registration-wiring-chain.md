## 5. DI Registration & Wiring Chain

The production DI wiring pattern (reconstructed from test initializers and consumer constructors):

### Step 1: Infrastructure Services
```
services.AddSingleton<IMemoryCache>(memoryCache)        // L1: MemoryCache with size limit, TTL, stats
services.AddSingleton<IDistributedCache>(fileCache)       // L2: CsvFileCache with folder, TTL
services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())  // Custom JSON serializer
services.AddHybridCache()                                 // Microsoft's HybridCache DI registration
```

Evidence: HybridCacheFactoryTests.cs:39-75, HybridCacheTests.cs:38-79, HybridCacheNullTests.cs:47-84, ActionPlanInstanceCacheTest.cs:46-91

### Step 2: Resolve Core Dependencies
```
HybridCache innerCache = provider.GetRequiredService<HybridCache>()
```
The Microsoft `AddHybridCache()` registers its internal implementation which wires `IMemoryCache` (L1) and `IDistributedCache` (L2) together.

### Step 3: Factory Registration
```
IHybridCacheFactory = new HybridCacheFactory(innerCache, memoryCache, distributedCache, configDict)
```
Evidence: ActionPlanInstanceCacheTest.cs:93-97

### Step 4: Typed Cache Creation (at consumer construction time)

**BufferedECEClient.cs:67-71** — ActionPlanInstance cache:
```
actionPlanInstanceCache = cacheFactory.CreateCache<ActionPlanInstance>(
    actionPlanInstanceExpirationTime,
    actionPlanInstanceCacheSettings.BackgroundRefreshSettings.ValueFactoryTimeout)
```

**BufferedECEClient.cs:79-88** — CloudParametersXml cache (via AutoCacheECEResult wrapper):
```
cloudParametersXML = new AutoCacheECEResult<CloudParametersXml>(
    getMethod,
    cacheFactory.CreateCache<CloudParametersXml>(
        cloudParamsExpirationTime,
        cloudParamsCacheSettings.BackgroundRefreshSettings.ValueFactoryTimeout))
```

### Step 5: Configuration Resolution

**BufferedECEClient.cs:62-66** — Per-type config override pattern:
```csharp
var actionPlanInstanceCacheSettings = CacheSettings.GetCacheSettings<ActionPlanInstance>(configDict);
var expirationTime = actionPlanInstanceCacheSettings.IsDefault
    ? settings.ActionPlanInstanceExpirationTime                           // fallback
    : (actionPlanInstanceCacheSettings.MemoryCacheSettings.TimeToLive     // type-specific override
       ?? settings.ActionPlanInstanceExpirationTime);
```

Config key naming convention (from HybridCacheFactory.cs:83-102):
- `"MemoryCacheTimeToLive_ActionPlanInstance"` (via `CacheConstants.TypeSettingSuffix<T>()`)
- `"FileCacheTimeToLive_ActionPlanInstance"`
- `"BackgroundValueFactoryTimeout_ActionPlanInstance"` OR `"BackgroundRefreshStalenessThreshold_ActionPlanInstance"`

Config source is `Dictionary<string, string>` populated from Service Fabric `Settings.xml` → `ServiceConfigSection` parameters (DefaultHybridCache.cs:793-808).

### MemoryCache Configuration (from tests)
**HybridCacheFactoryTests.cs:40-50** / **ActionPlanInstanceCacheTest.cs:49-60**:
```csharp
new MemoryCacheOptions {
    Clock = clock,
    SizeLimit = globalSettings.MemoryCacheSettings.SizeLimit,
    TrackStatistics = globalSettings.ReportCacheStats,
    TrackLinkedCacheEntries = false,
    CompactionPercentage = globalSettings.MemoryCacheSettings.CompactionPercentage,
    ExpirationScanFrequency = globalSettings.MemoryCacheSettings.TimeToLive.HasValue
        ? TimeSpan.FromTicks(TTL.Ticks / 2)
        : CacheConstants.DefaultMemoryCacheTimeToLive
}
```

---