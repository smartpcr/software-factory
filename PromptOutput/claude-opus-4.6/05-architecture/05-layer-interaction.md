## Layer Interaction

```
Controllers (11 controllers, 50+ ARM endpoints, mTLS auth, OData)
  ↓
Handlers + Managers (business rules, state machines, CacheInvalidator)
  ↓
ResourceResolver + Aggregators (cache-first, hash-based query isolation)
  ↓
HybridCache (L1 Memory + L2 File, background refresh, 9 serializers)
  ↓
External Clients (ECE, Download Service, DRP, LCM inter-node)
  ↓
Infrastructure (Service Fabric, ETW logging, Unity DI, CSV/Registry)
```