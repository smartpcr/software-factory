Let me now read all core source files in parallel to build the complete map.



Now let me continue reading the remaining DefaultHybridCache methods and the serialization layer:

Now let me read the DI registration, file cache, consumers, and other key files:
Now let me read the UnityConfig (DI registration), the cache controller, the consumers, and the event logger:
Now let me read the consumers and the CacheController, event logger, and remaining files:

Now let me look at the remaining key consumers and the CacheEventSource:
Let me now read the remaining consumers and the ResourceResolver:
Now let me look at the remaining critical files - the CacheEventSource, the UnityConfig full registration chain, and the OsUpdate consumer:
Now let me get the test file listing and look at key tests and the AutoCacheECEResult:
Let me check the JSON converters registration on models and look at the UnityConfig full registration chain:
Let me read the full `RegisterCaching` method and the beginning of the UnityConfig:
Now I have enough information to generate the comprehensive map. Let me create the plan file with the full analysis:

The complete HybridCache feature map has been generated. You can view the full document with `Ctrl+Y`. Here's a quick summary of the key findings:

**Architecture**: Custom two-tier cache wrapping MS `HybridCache` — L1 (MemoryCache, in-process) + L2 (CsvFileCache, cluster shared volume files).

**6 cached domain types**: `Update`, `UpdateRun`, `UpdateLocation`, `DiscoveryDiagnosticInfo`, `ActionPlanInstance`, `CloudParametersXml` — each with dedicated `DefaultHybridCache<T>` instances registered in `HybridCacheFactory.cacheInstances` (a `ConcurrentDictionary<Type, IHybridCache>`).

**Serialization dispatch**: `SerializerFactory.TryCreateSerializer<T>()` uses a priority-based type matching chain — 4 model types get Newtonsoft JSON with custom converters registered at startup, ECE types get System.Text.Json, internal types get BinaryFormatter with namespace whitelisting.

**Critical invariant**: Lists are NEVER stored as serialized collections. They're decomposed into individual items with `CacheKeyMapping` tracking item↔list relationships. Hash-based isolation ensures items only sync with lists sharing the same hash.

**Event-driven invalidation**: `CacheInvalidator` publishes 12 event types; `BufferedECEClient` subscribes to 4 of them for reactive cache updates.