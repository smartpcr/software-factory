## 1. Registration & Wiring

### 1.1 Application Entry Point — `UnityConfig.cs`

**File**: `src\UpdateResourceProvider\UpdateService\Service\Source\UnityConfig.cs`

The entire DI wiring for HybridCache originates from the static method `RegisterCaching()` (lines ~456–588). The call chain is:

```
UnityConfig.RegisterTypes()
  → UnityConfig.RegisterCaching(container, configDict)  // line ~456
  → Type-specific CacheSettings populated (lines 471–513)
  → ServiceCollection built, HybridCache resolved (lines 515–573)
  → Unity registrations for singletons (lines 575–587)
  → Resource handler DI registrations (lines 152–207)
```

### 1.2 Global Cache Infrastructure Registration (lines 515–587)

```csharp
// Line 515-517: ServiceCollection created, SystemClock registered
var services = new ServiceCollection();
var clock = new SystemClock();
services.AddSingleton<ISystemClock>(clock);

// Line 518-530: MemoryCache (L1) created with settings
var memoryCacheOptions = new MemoryCacheOptions()oryCache = new MemoryCache(memoryCacheOptions);
services.AddSingleton<IMemoryCache>(memoryCache);               // line 531

// Line 533-534: CsvFileCache (L2) created with settings
var fileCache = new CsvFileCache(cacheSettings.FileCacheSettings, clock);
services.AddSingleton<IDistributedCache>(fileCache);             // line 534

// Line 536-541: SerializerFactory created with custom converters
var serializerFactory = new SerializerFactory();
serializerFactory.RegisterConverter<Update>(new UpdateJsonConverter());
serializerFactory.RegisterConverter<UpdateRun>(new UpdateRunJsonConverter());
serializerFactory.RegisterConverter<UpdateLocation>(new UpdateLocationJsonConverter());
serializerFactory.RegisterConverter<DiscoveryDiagnosticInfo>(new DiscoveryDiagnosticInfoJsonConverter());
services.AddSingleton<IHybridCacheSerializerFactory>(serializerFactory); // line 541

// Line 553-570: Microsoft.Extensions.Caching.Hybrid registration
services.AddHybridCache(options =>imumPayloadBytes = cacheSettings.MaximumPayloadBytes;  // 100 MB
    options.MaximumKeyLength = 260;
    options.DefaultEntryOptions = new HybridCacheEntryOptions()

// Line 572-573: Build ServiceProvider and resolve the singleton HybridCache
var serviceProvider = services.BuildServiceProvider();
var hybridCache = serviceProvider.GetRequiredService<HybridCache>();

// Lines 575-587: Unity container registrations
container.RegisterInstance<IHybridCacheSerializerFactory>(serializerFactory);
container.RegisterInstance<IMemoryCache>(memoryCache);
container.RegisterInstance<IDistributedCache>(fileCache);
container.RegisterInstance<HybridCache>(hybridCache);

// Line 579-584: HybridCacheFactory created and registered
var hybridCacheFactory = new HybridCacheFactory(hybridCache, memoryCache, fileCache, configDict);
container.RegisterInstance<IHybridCacheFactory>(hybridCacheFactory);

// Line 586-587: CacheInvalidator created and registered
ICacheInvalidator cacheInvalidator = new CacheInvalidator(hybridCacheFactory);
container.RegisterInstance<ICacheInvalidator>(cacheInvalidator);
```

### 1.3 Type-Specific Cache Settings (lines 471–513)

Before infrastructure is built, **per-type settings** are populated into `configDict`:

| Type | TTL | BG Refresh | VF Timeout | Staleness | File/Line |
|------|-----|-----------|-----------|-----------|-----------|
| `DiscoveryDiagnosticInfo` | 30 min | enabled | 3 min | 10 sec | 471–477 |
| `Update` | 30 min | enabled | 3 min | 10 sec | 478–484 |
| `UpdateRun` | 30 min | enabled | 3 min | 10 sec | 485–491 |
| `UpdateLocation` | 30 min | enabled | 3 min | 10 sec | 492–498 |
| `ActionPlanInstance` | From `BufferedECEClientSettings.Default` | enabled | 3 min | 10 sec | 503–509 |
| `CloudParametersXml` | From `BufferedECEClientSettings.Default` | default | default | default | 510–513 |

### 1.4 Per-Type `IHybridCache<T>` Registrations

Each resource handler has a static `RegisterDependencyInjectedTypes()` method that registers:
1. `IResourceAggregator<T>` → concrete aggregator
2. `IHybridCache<T>` → `DefaultHybridCache<T>` (ContainerControlledLifetimeManager = singleton)
3. Registers the cache instance with `IHybridCacheFactory`
4. `IResourceResolver<T>` → `ResourceResolver<T>` (injected with the cache + aggregator + timeout)

| Type `T` | Handler File | Line | Aggregator |
|-----------|-------------|------|-----------|
| `Update` | `UpdateResourceHandler.cs` | 154–177 | `UpdateResourceAggregator` |
| `UpdateRun` | `UpdateRunResourceHandler.cs` | 63–86 | `UpdateRunAggregator` |
| `UpdateLocation` | `UpdateLocationResourceHandler.cs` | 61–85 | `UpdateLocationAggregator` |
| `DiscoveryDiagnosticInfo` | `DiscoveryDiagnosticInfoHandler.cs` | 42–66 | `DiscoveryDiagnosticInfoAggregator` |

Each follows the identical pattern:
```csharp
container.RegisterType<IHybridCache<T>, DefaultHybridCache<T>>(
    new ContainerControlledLifetimeManager(),
    new InjectionConstructor(
        container.Resolve<HybridCache>(),      // inner HybridCache singleton
        container.Resolve<IMemoryCache>(),      // MemoryCache singleton
        container.Resolve<IDistributedCache>(), // CsvFileCache singleton
        cacheSettings));                         // per-type CacheSettings
var hybCache = container.Resolve<IHybridCache<T>>();
var cacheFactory = container.Resolve<IHybridCacheFactory>();
cacheFactory.RegisterHybridCache(hybCache);     // registers into factory's ConcurrentDictionary<Type, IHybridCache>
```

### 1.5 Factory-Created Caches (via `IHybridCacheFactory`)

Two consumers use `cacheFactory.CreateCache<T>()` instead of Unity registration:

| Consumer | Type `T` | File | Line |
|----------|----------|------|------|
| `BufferedECEClient` | `ActionPlanInstance` | `BufferedECEClient.cs` | 71 |
| `BufferedECEClient` | `CloudParametersXml` | `BufferedECEClient.cs` | 91 |
| `HealthCheckHandler` | `ActionPlanInstance` | `HealthCheckHandler.cs` | 71 |

`HybridCacheFactory.CreateCache<T>()` (file `HybridCacheFactory.cs` line 42):
- Uses `cacheInstances.GetOrAdd(typeof(T), ...)` — returns existing or creates new `DefaultHybridCache<T>`
- Calls `PopulateConfig<T>()` to set type-specific TTL/timeout/staleness in `configDict`
- Resolves `CacheSettings.GetCacheSettings<T>(configDict)` for type-specific config

### 1.6 Configuration Binding

**File**: `CacheConstants.cs`

All settings are read from a flat `Dictionary<string, string>` (the `configDict`). Configuration keys follow the pattern:

```
Cache/Memory/TimeToLive                     → global memory TTL
Cache/Memory/TimeToLive/Update              → type-specific memory TTL for Update
Cache/File/TimeToLive/ActionPlanInstance     → type-specific file TTL for ActionPlanInstance
Cache/BackgroundRefreshEnabled/UpdateRun     → type-specific bg refresh flag
Cache/ValueFactoryTimeout/UpdateLocation    → type-specific value factory timeout
Cache/StalenessThreshold/Update             → type-specific staleness threshold
```

The `configDict` is loaded from Service Fabric's `Settings.xml` configuration package. `CacheSettings.GetCacheSettings<T>()` appends `TypeSettingSuffix<T>()` (= `"Type`) to each key, enabling per-type overrides.

**Settings.xml persistence**: `CacheSettings.ExportSettings()` (line 955 of `DefaultHybridCache.cs`) reads `Settings.xml` from the working directory, and `ImportSettings()` (line 985) writes updated settings back.

### 1.7 Lookup Tables Populated at Startup

**`HybridCacheFactory.cacheInstances`** (`HybridCacheFactory.cs` line 24):
```csharp
private readonly ConcurrentDictionary<Type, IHybridCache> cacheInstances = new();
```
- **Populated by**: `RegisterHybridCache()` (line 72), `CreateCache<T>()` (line 42), `CreateListCache<T>()` (line 55)
- **Read by**: `TryGet<T>()` (line 78), `TryGet(string typeName)` (line 84), `GetFirstOrDefault()` (line 90)
- **Consumed by**: `CacheInvalidator`, `CacheControllerImplementation`, `BufferedECEClient`, `HealthCheckHandler`

**`SerializerFactory.typeSpecificSettings`** (`SerializerFactory.cs` line 27):
```csharp
private readonly ConcurrentDictionary<Type, JsonSerializerSettings> typeSpecificSettings = new();
```
- **Populated by**: `RegisterConverter<T>()` — called in `UnityConfig` lines 537–540 for `Update`, `UpdateRun`, `UpdateLocation`, `DiscoveryDiagnosticInfo`
- **Read by**: `TryCreateSerializer<T>()` — checked at line ~72 before falling through to namespace-based selection

---