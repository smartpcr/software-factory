## 3. Storage & Persistence Layers

### 3.1 Multi-Tier Architecture

```
┌─────────────────────────────────────────────────┐
│  Caller (ResourceResolver, BufferedECEClient)   │
└─────────────┬───────────────────────────────────┘
              │  IHybridCache<T>
              ▼
┌─────────────────────────────────────────────────┐
│  DefaultHybridCache<T>   (wrapper)              │
│  - Item/list mappings (CacheKeyMapping)         │
│  - Background refresh logic                     │
│  - Cache hit/miss logging                       │
│  - Settings-driven enable/disable               │
└─────────────┬───────────────────────────────────┘
              │  Microsoft.Extensions.Caching.Hybrid.HybridCache
              ▼
┌───────────────────────────────────┐
│  L1: MemoryCache (IMemoryCache)   │ ◄── in-process, ~100MB limit
│  - MemoryCacheOptions:            │     per-type TTL, compaction 5%
│  - TrackStatistics: true          │     expiration scan = TTL/2
└────────────┬──────────────────────┘
             │  L1 miss
             ▼
┌───────────────────────────────────┐
│  L2: CsvFileCache                  │ ◄── file-backed, cluster shared volume
│  (IDistributedCache)               │     C:\ClusterStorage\...\Caches
│  - Per-file SemaphoreSlim locking  │     TTL + purge expiration
│  - Atomic writes (temp + rename)   │     8-byte header (expiration ticks)
│  - Retry reads (3 attempts)        │     per-file concurrency
└────────────────────────────────────┘
```

### 3.2 L1: MemoryCache

**Created**: `UnityConfig.cs` line 530
- `SizeLimit`: 100 MB (from `cacheSettings.MemoryCacheSettings.SizeLimit`)
- `CompactionPercentage`: 0.05 (5%)
- `TrackStatistics`: true
- `ExpirationScanFrequency`: TTL/2

### 3.3 L2: CsvFileCache

**File**: `CsvFileCache.cs`

**Cache folder**: `C:\ClusterStorage\Infrastructure_1\Shares\SU1_Infrastructure_1\Updates\Caches` (production default, `CacheConstants.DefaultRemoteCacheFolder`)

**Key → File Path mapping** (line 412):
```csharp
// "List/Update/somehash" → "C:\...\Caches\List\Update\somehash"
// Slashes become directory separators
internal static string CacheKeyToFilePath(string cacheKey, string cacheFolder)
```

**File format**: 8-byte header (absolute expiration as UTC ticks) + payload bytes.

**Expiration**: Dual mechanism:
1. **TTL**: `absoluteExpiration` stored in header, or calculated from file creation time + TTL
2. **Purge**: If `PurgeExpirationInDays` set, files older than N days are deleted

**Write path** (`SetAsync`, line 146):
1. Compute absolute expiration from `DistributedCacheEntryOptions`
2. Build header (8 bytes) + payload
3. Acquire per-file `SemaphoreSlim`
4. Atomic write: write to temp file, then rename
5. Schedule async purge of old files

**Read path** (`GetAsync`, line 49):
1. Check file exists → cache miss if not
2. Acquire per-file `SemaphoreSlim`
3. Read file with retry (3 attempts, 100ms delay)
4. Parse 8-byte header → check TTL and purge expiration
5. If expired → schedule async delete, return null (cache miss)
6. Extract payload after header

### 3.4 Item-List Mapping System

**File**: `CacheKeyMapping.cs`

`DefaultHybridCache<T>` never stores lists directly. Instead:
- Individual items are stored with `HybridCache.SetAsync()`
- A bidirectional in-memory mapping tracks which items belong to which lists

```csharp
// Two dictionaries:
ConcurrentDictionary<CacheKey, HashSet<CacheKey>> itemToLists;  // item → lists it belongs to
ConcurrentDictionary<CacheKey, HashSet<CacheKey>> listToItems;  // list → items in it
```

**Thread safety**: Operations spanning both dictionaries use `lock(syncLock)`.

### 3.5 CacheKey Structure

**File**: `CacheKey.cs` — `readonly struct CacheKey : IEquatable<CacheKey>`

FormatArmName` for items, `ListentA
Optional hash suffix-char-hex-` for query-specific caching.

Factory methods:
- `CacheKey.ForItem<T>(armName, queryHash)`
- `CacheKey.ForList<T>(parentArmName, queryHash)`

---