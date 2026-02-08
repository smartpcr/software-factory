## Data Flow

```
READ PATH:
  Portal/CLI → REST API (mTLS) → Controller → Handler → ResourceResolver<T>
    → Cache hit? Return (+ background refresh if stale)
    → Cache miss? → Aggregator combines ECE + Discovery + Providers → Cache → Return

WRITE PATH (apply/prepare/checkHealth):
  REST API → Controller → Handler → Manager
    → TransactionManager (Reliable Collections)
    → ECE Client (creates action plan)
    → CacheInvalidator.Raise*() (event-driven cache update)
    → Returns 202 Accepted (async long-running operation)

DISCOVERY (background, every 1 hour):
  Timer → UpdateDiscoveryManager
    → ComponentDiscovery (signed XML manifest)
    → SbeDiscovery (SBE/OEM manifest)
    → LocalFolderDiscovery
    → CacheInvalidator.RaiseRefreshUpdates()
```

---