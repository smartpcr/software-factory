## 13. Complete Dependency Graph

```
                         ┌──────────────────────┐
                         │   Service Fabric      │
                         │   Settings.xml        │
                         │  (ServiceConfigSection)│
                         └──────────┬─────────────┘
                                    │ config keys
                                    ▼
                         ┌──────────────────────┐
                         │  Dictionary<str,str>  │ configDict
                         │  CacheSettings.Get*() │
                         └──────────┬─────────────┘
                                    │
           ┌────────────────────────┼────────────────────────┐
           ▼                        ▼                        ▼
  ┌─────────────────┐    ┌──────────────────┐    ┌───────────────────┐
  │ MemoryCacheSettings│  │FileCacheSettings │    │BackgroundRefresh  │
  │ .TimeToLive       │  │.TimeToLive       │    │Settings           │
  │ .SizeLimit         │  │.CacheFolder      │    │.ValueFactoryTimeout│
  │ .IsDisabled        │  │.IsDisabled       │    │.StalenessThreshold│
  └────────┬──────────┘  └────────┬─────────┘    └────────┬──────────┘
           │                      │                        │
           ▼                      ▼                        │
  ┌────────────────┐    ┌─────────────────┐                │
  │ IMemoryCache   │    │IDistributedCache│                │
  │ (MemoryCache)  │    │ (CsvFileCache)  │                │
  │    L1           │    │    L2           │                │
  └────────┬───────┘    └────────┬────────┘                │
           │                      │                        │
           ▼                      ▼                        │
  ┌─────────────────────────────────────────┐              │
  │ Microsoft.Extensions.Caching.Hybrid     │              │
  │ HybridCache (AddHybridCache())          │◄─────────────┘
  │ + IHybridCacheSerializerFactory         │
  │   (SerializerFactory)                   │
  └─────────────────────┬──────────────────┘
                        │
                        ▼
           ┌─────────────────────────┐
           │   HybridCacheFactory    │  IHybridCacheFactory
           │   .cacheInstances       │  ConcurrentDict<Type, IHybridCache>
           │   .CreateCache<T>()     │──► DefaultHybridCache<T>
           │   .CreateListCache<T>() │──► DefaultHybridCache<T>
           │   .TryGet<T>()         │
           │   .TryGet(string)      │
           └──────────┬─────────────┘
                      │ creates
                      ▼
           ┌──────────────────────────────┐
           │  DefaultHybridCache<T>       │  IHybridCache<T>
           │  .GetOrAddAsync()            │
           │  .SetAsync()                 │
           │  .GetOrAddListAsync()        │
           │  .SetListAsync()             │
           │  .ClearAsync()               │
           │  .cacheKeyMapping            │──► CacheKeyMapping
           │  .cacheKeys                  │    (list↔item maps)
           │  .ShouldBackgroundRefresh()  │
           │  .TriggerBackgroundRefresh() │
           └──────────┬──────────────────┘
                      │ consumed by
          ┌───────────┼───────────────┐
          ▼                           ▼
┌──────────────────┐      ┌────────────────────────┐
│ BufferedECEClient│      │ AutoCacheECEResult<T>  │
│ .actionPlanCache │──────│ .cache: IHybridCache<T>│
│ .cloudParamsXML  │──►   │ .GetValueAsync()       │
│ + CacheInvalidator      │ .UpdateValueInBg()     │
└──────────────────┘      └────────────────────────┘
```

---