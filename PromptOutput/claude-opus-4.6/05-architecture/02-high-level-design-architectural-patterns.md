## High-Level Design & Architectural Patterns

This is an **ARM-compatible REST service** built on **.NET Framework 4.72**, hosted as a **Service Fabric stateful service** on Azure Stack HCI cluster nodes. It manages the full update lifecycle: discovery, preparation, health checks, and installation.

**Core patterns:**

| Pattern | Description |
|---|---|
| **Cache-First** | All reads go through `ResourceResolver<T>` → `HybridCache` (L1 memory + L2 CSV file) before hitting external services |
| **Aggregator** | Each resource type has an aggregator combining data from ECE, discovery providers, and cached state |
| **Contract/Source** | Every component is split into `Contract` (interfaces/DTOs) and `Source` (implementation) projects |
| **Handler** | Controllers delegate to handler classes that encapsulate business logic |
| **Event-Driven Invalidation** | `CacheInvalidator` raises 12 domain events that subscribers react to for cache updates |