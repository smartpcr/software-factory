## 14. Serialization & Type Handling

### 14.1 Custom Serializer Factory: `SerializerFactory`

**Source file not provided** but imported and used from namespace `Microsoft.AzureStack.Services.Update.Common.Serialization`.

**Interface implemented:** `IHybridCacheSerializerFactory` (from `Microsoft.Extensions.Caching.Hybrid`). This Microsoft-defined interface has a single method: `bool TryCreateSerializer<T>(out IHybridCacheSerializer<T> serializer)`.

**Every DI registration site:**

| File | Line | Registration |
|------|------|-------------|
| `HybridCacheFactoryTests.cs` | 77 | `services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())` |
| `HybridCacheNullTests.cs` | 79 | `services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())` |
| `HybridCacheTests.cs` | 75 | `services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())` |
| `ActionPlanInstanceCacheTest.cs` | 94 | `services.AddSingleton<IHybridCacheSerializerFactory>(new SerializerFactory())` |

Every site follows the identical pattern: construct `new SerializerFactory()` then register as singleton. **No constructor parameters** are passed, meaning the factory carries no external configuration or registry map — type selection logic is entirely internal to `SerializerFactory`.

### 14.2 How the Serializer Factory Is Resolved at Runtime

The runtime chain is:

1. **`services.AddHybridCache()`** — called at every DI setup site (same four files listed above, always immediately after registering the serializer factory). This registers `Microsoft.Extensions.Caching.Hybrid.HybridCache` as a service.

2. **`provider.GetRequiredService<HybridCache>()`** — resolves the singleton `HybridCache` instance. During construction, the HybridCache infrastructure discovers all registered `IHybridCacheSerializerFactory` instances from DI. Since `SerializerFactory` is registered as `IHybridCacheSerializerFactory`, HybridCache will call `SerializerFactory.TryCreateSerializer<T>()` whenever it needs to serialize/deserialize type `T` for the distributed cache (L2) boundary.

3. **The resolved `HybridCache` is injected into `DefaultHybridCache<T>`** — see `DefaultHybridCache.cs:74-78`:
   ```
   public DefaultHybridCache(HybridCache cache, IMemoryCache memoryCache, IDistributedCache distributedCache, CacheSettings cacheSettings)
   ```
   Also injected into `HybridCacheFactory` at `HybridCacheFactory.cs:24-28`.

**Critical path:** The serializer is never called directly by application code. It is called internally by `HybridCache` when data crosses the **L1 (memory) → L2 (distributed/file) boundary**. Specifically:

- **Serialize** occurs inside `HybridCache.SetAsync()` and `HybridCache.GetOrCreateAsync()` when writing to L2.
- **Deserialize** occurs inside `HybridCache.GetOrCreateAsync()` when reading from L2 on an L1 miss.

### 14.3 Serialization Trigger Points in `DefaultHybridCache<T>`

Every call to the inner `this.hybridCache` in `DefaultHybridCache.cs` triggers serialization/deserialization:

| Method | Line(s) | Inner Call | Serialization Direction |
|--------|---------|-----------|------------------------|
| `GetFromCacheAsync` | 107-113 | `hybridCache.GetOrCreateAsync<T>(key.FullKey, ...)` | Deserialize on L2 hit |
| `GetOrAddAsync` | 148-160 | `hybridCache.GetOrCreateAsync<T>(key.FullKey, ...)` | Serialize on L2 write; Deserialize on L2 hit |
| `SetAsync` | 199-204 | `hybridCache.SetAsync(key.FullKey, value, ...)` | Serialize to L2 |
| `ExistsAsync` | 227-232 | `hybridCache.GetOrCreateAsync<T>(key.FullKey, ...)` | Deserialize on L2 hit |
| `RemoveAsync` | 238-239 | `hybridCache.RemoveAsync(key.FullKey, ...)` | No serialization |
| `CacheListItems` | 303-313 | `hybridCache.SetAsync(itemCacheKey.FullKey, item, ...)` | Serialize each item to L2 |
| `ClearAsync` | 273 | `hybridCache.RemoveAsync(cacheKey.FullKey, ...)` | No serialization |

Note at `DefaultHybridCache.cs:112-113`: the `readonlyCacheEntryOptions` uses flags `DisableDistributedCacheWrite | DisableLocalCacheWrite`, meaning `GetFromCacheAsync` and `ExistsAsync` never **write** to L2, but still **read** (and thus deserialize) from it.

### 14.4 The L2 Distributed Cache: `CsvFileCache`

`CsvFileCache` implements `IDistributedCache` — it receives/returns raw `byte[]`. The serialization has already happened by the time data reaches `CsvFileCache`.

**Registration sites:**

| File | Line | Pattern |
|------|------|---------|
| `HybridCacheFactoryTests.cs` | 72 | `new CsvFileCache(globalSettings.FileCacheSettings, this.clock)` |
| `HybridCacheNullTests.cs` | 71-72 | `new CsvFileCache(cacheSettings.FileCacheSettings, this.clock)` |
| `HybridCacheTests.cs` | 68-69 | `new CsvFileCache(cacheSettings.FileCacheSettings, this.clock)` |
| `ActionPlanInstanceCacheTest.cs` | 85-86 | `new CsvFileCache(globalSettings.FileCacheSettings, this.clock)` |

All registered as `services.AddSingleton<IDistributedCache>(this.fileCache)`.

`CsvFileCache` source is not in the provided files, but it is referenced for statistics at `DefaultHybridCache.cs:401-411` (cast `this.distributedCache is CsvFileCache fileCache`) and for file cleanup at `DefaultHybridCache.cs:281-291` (cast `this.distributedCache is CsvFileCache csvFileCache`; calls `csvFileCache.ClearAllFiles(forceDelete: true)`).

### 14.5 `HybridCacheEntryOptions` — Controls Whether Serialization Occurs

`HybridCacheOptionExtensions.cs:13-26` defines `WithTtl()` extension method that constructs new `HybridCacheEntryOptions` preserving `Flags`. The flags control whether L1/L2 are enabled:

- `HybridCacheEntryFlags.DisableDistributedCache` — **skips serialization entirely** (no L2 read or write)
- `HybridCacheEntryFlags.DisableDistributedCacheWrite` — skips serialize-on-write but still deserializes on read
- `HybridCacheEntryFlags.DisableLocalCache` — skips L1, forces L2 (forces serialization)

These flags are configured at `DefaultHybridCache.cs:350-370` in `RefreshCacheSettings()`:
- `cacheEntryOptions` — normal read/write, flags based on `cacheSettings.MemoryCacheSettings.IsDisabled` and `cacheSettings.FileCacheSettings.IsDisabled`
- `readonlyCacheEntryOptions` — always `DisableDistributedCacheWrite | DisableLocalCacheWrite`

Test at `HybridCacheTests.cs:166-174` explicitly uses `DisableLocalCacheWrite` to force L2-only write (triggering serialization).
Test at `HybridCacheTests.cs:188-194` explicitly uses `DisableDistributedCacheWrite` to prevent L2 write (skipping serialization).

### 14.6 Model Classes Participating in Serialization

Every type `T` used as a generic parameter in `DefaultHybridCache<T>` or `IHybridCache<T>` must be serializable by `SerializerFactory`. The constraint is `where T : ICachedItem`.

| Type | Used In | Namespace |
|------|---------|-----------|
| `Customer` | `HybridCacheTests.cs:31`, `HybridCacheNullTests.cs:42` | `Microsoft.AzureStack.Services.Update.Common.UnitTest` |
| `Product` | `HybridCacheFactoryTests.cs:86` | `Microsoft.AzureStack.Services.Update.Common.UnitTest` |
| `ActionPlanInstance` | `BufferedECEClient.cs:30`, `ActionPlanInstanceCacheTest.cs` | `Microsoft.AzureStack.Services.Update.ECE.Client.Models` |
| `CloudParametersXml` | `BufferedECEClient.cs:31` | `Microsoft.AzureStack.Services.Update.ECE.Client.Models` |
| `TestFabricResource` | Both `MockedHybridCache.cs` files | `Microsoft.AzureStack.Services.Update.TestCommon.TestUtilities` |

Source files for `Customer`, `Product`, `ActionPlanInstance`, `CloudParametersXml`, and `TestFabricResource` are **not in the provided files**, so model-level annotations (`[JsonConverter]`, `[DataContract]`, etc.) cannot be confirmed from the provided context. However, since `SerializerFactory` is the sole `IHybridCacheSerializerFactory`, it must handle all these types.

### 14.7 Mock Serialization Bypass

`MockHybridCache.cs` (in `UpdateService\Core\UnitTest\Caching\`) extends `HybridCache` directly and **bypasses all serialization**:

- `GetOrCreateAsync` at line 21-28: directly calls `factory(state, cancellationToken)` and returns the result — no L2 interaction, no serialization.
- `SetAsync` at line 30-31: throws `NotImplementedException`.
- `RemoveAsync` at line 35-36: throws `NotImplementedException`.

Both `MockedHybridCache` implementations (in `ResourceProvider\IntegrationTest` and `ResourceCaching\UnitTest\Resolver`) implement `IHybridCache<T>` directly and also bypass serialization — they call `valueFactory` directly and return results without cache interaction.

### 14.8 Complete Serialization Data Flow

```
Application Code
    │
    ▼
DefaultHybridCache<T>  ──(calls)──►  HybridCache (Microsoft)
    │                                       │
    │                                       ├─ L1: IMemoryCache
    │                                       │   (no serialization - objects stored by reference)
    │                                       │
    │                                       └─ L2: IDistributedCache (CsvFileCache)
    │                                           │
    │                              ┌────────────┤
    │                              ▼            │
    │                     SerializerFactory      │
    │                     .TryCreateSerializer<T>()
    │                              │            │
    │                              ▼            │
    │                     IHybridCacheSerializer<T>
    │                     .Serialize(T) → byte[]  ──────► CsvFileCache.SetAsync(byte[])
    │                     .Deserialize(byte[]) → T ◄───── CsvFileCache.GetAsync() → byte[]
```

### 14.9 Key Gaps in Provided Files

The following source files are critical to serialization but **not provided**:

| Missing File | Why It Matters |
|-------------|---------------|
| `SerializerFactory.cs` | Contains the actual `TryCreateSerializer<T>()` logic — determines JSON vs binary vs custom per type |
| `CsvFileCache.cs` | The L2 implementation that stores/retrieves `byte[]` as CSV files |
| `ICachedItem.cs` | Defines the interface all serializable cache models must implement |
| `Customer.cs`, `Product.cs` | Test models — may have `[JsonConverter]` or `[DataContract]` annotations |
| `ActionPlanInstance.cs`, `CloudParametersXml.cs` | Production models — likely have serialization attributes |

**Without `SerializerFactory.cs`**, the exact serialization format (JSON, MessagePack, protobuf, etc.) and any type-specific converter selection logic cannot be confirmed from the provided code alone. All four DI registration sites construct it with `new SerializerFactory()` (no arguments), suggesting either a convention-based approach (e.g., always JSON) or an internal type map.

---