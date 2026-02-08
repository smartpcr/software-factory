## High-Level Design & Architectural Patterns

The URP is an **ARM-compatible REST service** running as a **Service Fabric stateful service** on Azure Stack HCI cluster nodes (.NET Framework 4.72). It manages the full update lifecycle: discovery, preparation, health checks, and installation.

**Core patterns:**

| Pattern | Description |
|---|---|
| **Cache-First** | All reads go through `ResourceResolver<T>` → `HybridCache` (L1 memory + L2 CSV file) before hitting aggregators |
| **Aggregator** | Each resource type has an aggregator combining ECE, discovery, and provider data into a unified ARM resource |
| **Contract/Source** | Every component splits into `Contract/` (interfaces + DTOs) and `Source/` (implementation) projects |
| **Handler** | Controllers delegate to Handler classes wrapping `ResourceResolver<T>` for business logic |
| **Event-Driven Invalidation** | `CacheInvalidator` raises 12 domain events that subscribers react to for cache updates |
| **Background Operations** | Timer-based periodic tasks (discovery every 1h, ECE refresh, telemetry every 24h) |

---