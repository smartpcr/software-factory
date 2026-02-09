## 6. Configuration Model

### `CacheSettings` (referenced, not fully provided)
Properties used throughout the codebase:

| Property | Type | Referenced At |
|---|---|---|
| `IsDisabled` | `bool` | DefaultHybridCache.cs:660 |
| `IsDefault` | `bool` | BufferedECEClient.cs:64 |
| `MemoryCacheSettings` | `MemoryCacheSettings` | DefaultHybridCache.cs:664, 670 |
| `FileCacheSettings` | `FileCacheSettings` | DefaultHybridCache.cs:666, 672 |
| `BackgroundRefreshSettings` | `BackgroundRefreshSettings` | DefaultHybridCache.cs:697, 618 |
| `ReportCacheStats` | `bool` | DefaultHybridCache.cs:733 |

Static methods:
- `CacheSettings.GetCacheSettings(Dictionary<string,string>)` → global settings
- `CacheSettings.GetCacheSettings<T>(Dictionary<string,string>)` → type-specific settings
- `cacheSettings.AddToConfig<T>(Dictionary<string,string>)` → write settings to config
- `cacheSettings.SetCacheSettingsToXml<T>(string)` → persist to XML file

### `MemoryCacheSettings` (referenced)

| Property | Used At |
|---|---|
| `TimeToLive` | DefaultHybridCache.cs:670, HybridCacheFactoryTests.cs:88 |
| `SizeLimit` | HybridCacheFactoryTests.cs:43 |
| `CompactionPercentage` | HybridCacheFactoryTests.cs:46 |
| `IsDisabled` | DefaultHybridCache.cs:664 |

### `FileCacheSettings` (referenced)

| Property | Used At |
|---|---|
| `TimeToLive` | DefaultHybridCache.cs:672, HybridCacheTests.cs:68 |
| `CacheFolder` | HybridCacheNullTests.cs:60 |
| `PurgeExpirationInDays` | HybridCacheTests.cs:69 |
| `IsDisabled` | DefaultHybridCache.cs:666 |

### `BackgroundRefreshSettings` (referenced)

| Property | Used At |
|---|---|
| `IsEnabled` | DefaultHybridCache.cs:697, 713 |
| `ValueFactoryTimeout` | DefaultHybridCache.cs:618, 636; HybridCacheNullTests.cs:67 |
| `StalenessThreshold` | DefaultHybridCache.cs:706, 722; HybridCacheNullTests.cs:68 |

---