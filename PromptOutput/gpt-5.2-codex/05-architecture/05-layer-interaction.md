## Layer Interaction

```
Controllers (11 controllers, 50+ endpoints, OData, mTLS)
  ↓
Handlers + Managers (business rules, state machine, CacheInvalidator writes)
  ↓
ResourceResolver + Aggregators (cache-first, hash-based query isolation)
  ↓
HybridCache (L1: MemoryCache 100MB/120min, L2: CsvFileCache on CSV 24h)
  ↓
External Clients (ECE, Download Service, DRP, LCM)
  ↓
Infrastructure (Service Fabric, ETW logging, Unity DI, Cluster Shared Volumes)
```

---