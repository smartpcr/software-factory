# Architecture

Comprehensive architecture overview of the Azure Stack HCI Update Resource Provider (URP).

---

## Table of Contents

1. [System Overview](#system-overview)
2. [High-Level Design](#high-level-design)
3. [Architectural Patterns](#architectural-patterns)
4. [Data Flow](#data-flow)
5. [Key Abstractions & Interfaces](#key-abstractions--interfaces)
6. [Layer Interaction](#layer-interaction)
7. [Deployment Model & Runtime](#deployment-model--runtime)

---

## System Overview

The Update Resource Provider (URP) is an **Azure Resource Manager (ARM)-compatible REST service** that runs as a **Service Fabric stateful service** on Azure Stack HCI cluster nodes. It manages the full update lifecycle: discovery, preparation, health checks, and installation.

- **Framework:** .NET Framework 4.72 (some projects dual-target .NET 8.0)
- **Hosting:** Service Fabric stateful service with OWIN/Web API 2.0
- **DI Container:** Unity
- **Serialization:** Newtonsoft.Json (API), multiple formats in cache (Binary, MessagePack, System.Text.Json, DataContract, XElement)
- **Logging:** ETW EventSource with static logger facades
- **Storage:** Reliable Collections + Cluster Shared Volumes + Windows Registry

---

## High-Level Design

```
                        Azure Portal / CLI / PowerShell
                                    │
                                    ▼
                    ┌───────────────────────────────────┐
                    │    ASP.NET Web API 2.0 (OWIN)      │
                    │    mTLS Certificate Auth            │
                    │    OData Query Support               │
                    └──────────┬────────────────────────┘
                               │
                    ┌──────────▼────────────────────────┐
                    │   Resource Controllers              │
                    │   (ARM-compatible REST API)          │
                    │   Update, UpdateRun, Location        │
                    └──────────┬────────────────────────┘
                               │
                    ┌──────────▼────────────────────────┐
                    │   ResourceResolver<T>               │
                    │   (Cache-first resolution)           │
                    └───┬──────────────────┬────────────┘
                        │                  │
              Cache HIT │          Cache MISS
                        │                  │
                        ▼                  ▼
               ┌─────────────┐   ┌──────────────────────┐
               │ HybridCache  │   │ Aggregator<T>         │
               │ L1: Memory   │   │ Combines ECE +        │
               │ L2: CSV File │   │ Discovery + State      │
               └─────────────┘   └────────┬─────────────┘
                                          │
                         ┌────────────────┼──────────────┐
                         ▼                ▼              ▼
                  ┌────────────┐  ┌────────────┐  ┌───────────┐
                  │ ECE Client  │  │ Discovery   │  │ Download   │
                  │ Action Plans│  │ Providers   │  │ Service    │
                  └────────────┘  └────────────┘  └───────────┘
```

### Core Components

| Component | Location | Responsibility |
|---|---|---|
| **Update Service** | `src/UpdateResourceProvider/UpdateService/` | REST API, business logic, models, discovery |
| **Common Libraries** | `src/Common/` | Caching (HybridCache), serialization, ARM framework, tracing |
| **ECE Client** | `src/EceClient/` | Enterprise Cloud Engine communication (action plans) |
| **Download Service Client** | `src/DownloadServiceClient/` | Package download from DCAT, CAU integration |
| **LCM Update Service Client** | `src/UpdateResourceProvider/LcmUpdateServiceClient/` | Inter-node cluster communication |
| **LCM PowerShell** | `src/UpdateResourceProvider/LcmPowerShell/` | 15+ cmdlets for local cluster management |
| **OS Update Role** | `src/UpdateResourceProvider/OsUpdateRole/` | Service Fabric stateful service for OS updates |
| **Standalone Discovery** | `src/UpdateResourceProvider/StandaloneDiscovery/` | Zero-touch provisioning (ZTP) SBE discovery |

### Module Dependencies

```
UpdateService.Controllers
       │
       ├──► UpdateService.Core (handlers, managers, aggregators)
       │         │
       │         ├──► UpdateService.Models (Update, UpdateRun, UpdateLocation, Step)
       │         │
       │         ├──► EceClient (action plan execution, stamp info)
       │         │
       │         ├──► DownloadServiceClient (package download, CAU)
       │         │
       │         └──► Common.ResourceCaching (ResourceResolver, BaseResourceAggregator)
       │                   │
       │                   └──► Common.Common (HybridCache, Serialization, CacheInvalidator)
       │
       └──► Common.ResourceProvider (ARM framework, routing, DI)
                │
                └──► Common.Trace (ETW EventSource, telemetry)
```

---

## Architectural Patterns

### 1. Cache-First Architecture

Every resource read goes through `ResourceResolver<T>`, which checks the two-tier `HybridCache` (L1 memory + L2 file) before calling the aggregator. This minimizes expensive ECE and external service calls.

```csharp
// ResourceResolver<T>.GetResource()
var cacheKey = CacheKey.ForItem<T>(key.ArmName, null);
var (result, exception) = await cache.GetOrAddAsync(
    cacheKey,
    async (ct) => await aggregator.GetResult(key, context, ct),
    timeout);
```

Stale items trigger fire-and-forget **background refresh** — the caller gets the cached value immediately while a background task updates the cache.

### 2. Aggregator Pattern

Each resource type has an aggregator that combines data from multiple sources into a unified ARM resource view:

| Aggregator | Sources Combined |
|---|---|
| `UpdateResourceAggregator` | Package providers (Component, OEM, Local), ECE state, discovery timing, download state, health check results, installed versions |
| `UpdateRunAggregator` | Preparation runs (Reliable Collections), installation runs (ECE action plans) |
| `UpdateLocationAggregator` | All updates, all runs, ECE system state, health check results, discovery timing |
| `DiscoveryDiagnosticInfoAggregator` | Discovery diagnostic data, non-applicable update reasons |

### 3. Contract/Source Separation

Every component is split into a `Contract` project (interfaces + DTOs) and a `Source` project (implementation), enforcing interface-based decoupling:

```
Component/
├── Contract/           # Interfaces and DTOs
│   ├── IHandler.cs
│   └── Component.Contract.csproj
├── Source/             # Implementation
│   ├── Handler.cs
│   └── Component.csproj
└── UnitTest/           # Tests (co-located)
    └── Component.UnitTest.csproj
```

### 4. Event-Driven Cache Invalidation

`CacheInvalidator` raises 12 domain events that aggregators and managers subscribe to, triggering cache updates without polling:

```
CacheInvalidator.RaiseRefreshUpdates()
  └─► OnRefreshUpdates?.Invoke(...)
      └─► UpdateResourceAggregator.OnRefreshUpdates (subscriber)
          └─► Re-fetches and caches fresh data
```

Events: `OnUpdateAdded`, `OnRefreshUpdate`, `OnRefreshUpdates`, `OnUpdateRunAdded`, `OnRefreshUpdateRun`, `OnRefreshActionPlanInstance`, `OnRefreshUpdateActionPlanInstances`, `OnMonitorUpdateInstallationActionPlanInstance`, `OnUpdateLocationSystemHealthCheckFinished`, `OnRefreshCloudParametersXml`, `OnRefreshDiscoveryConfig`, `OnFeatureFlagChanged`

### 5. Handler Pattern

Controllers delegate to handler classes which encapsulate business logic and wrap `ResourceResolver<T>` calls:

```
Controller (REST surface, no logic)
  → Handler (business rules, validation)
    → ResourceResolver<T> (cache-first data access)
      → Aggregator (multi-source combining)
```

### 6. Background Operation Pattern

Timer-based periodic tasks inherit from `BackgroundOperation` (abstract base using `System.Timers.Timer`), are instantiated as `Lazy<IBackgroundOperation>`, initialized with the Unity container in `StartAsync()`, and disabled in `StopAsync()`.

---

## Data Flow

### Read Path (GET requests)

```
Portal/CLI → REST API (mTLS auth)
  → Controller → Handler → ResourceResolver<T>
    → Cache hit? Return immediately (+ background refresh if stale)
    → Cache miss? → Aggregator combines:
        - ECE client (stamp versions, action plans, installed state)
        - Package providers (component, OEM, local storage)
        - Health check results
        - Download state
      → Cache result in L1 + L2 → Return to caller
```

### Write Path (POST apply/prepare/checkHealth)

```
Portal/CLI → REST API
  → Controller → Handler → Manager (creates workflow)
    → TransactionManager (writes to Reliable Collections)
    → ECE Client (creates action plan instance)
    → CacheInvalidator.Raise*() (notifies subscribers)
    → Background monitoring (polls action plan progress)
    → CacheInvalidator.InvalidateCache() (updates cache with new state)
  → Returns 202 Accepted (long-running async operation)
```

### Discovery Path (background, every 1 hour)

```
BackgroundUpdateDiscovery timer fires
  → UpdateDiscoveryManager orchestrates:
      - ComponentDiscovery → signed XML manifest → ComponentUpdateProvider
      - SbeDiscovery → SBE manifest → OemUpdateProvider
      - LocalFolderDiscovery → local folder scan → LocalStorageProvider
  → SignedXmlValidator validates manifest signatures
  → Package providers store discovered packages
  → DiscoveryDiagnosticCache records non-applicable updates + reasons
  → CacheInvalidator.RaiseRefreshUpdates()
```

### Update Installation Path

```
POST /updates/{name}/apply
  → UpdateResourceHandler validates update state
  → HealthCheckHandler runs pre-update checks (35 validators via ECE)
  → ECE Client creates installation action plan
  → UpdateRunAggregator monitors progress via ECE polling
  → UpdateInstallationProgressCalculator tracks step completion
  → On completion: UpdateStateIdentifier determines final state
  → CacheInvalidator invalidates all affected caches
```

### Health Check Path

```
POST /updates/{name}/checkHealth (or /updateLocations/{name}/checkHealth)
  → HealthCheckHandler → ECE action plan (CheckCloudHealth.xml)
    → CheckCloudHealth: 25 validators (Microsoft.EnvironmentReadiness.Validator)
    → EnvironmentValidatorPreUpdate: 10 validators (AzStackHci.EnvironmentChecker)
  → Results cached in HybridCache
  → CacheInvalidator.RaiseUpdateLocationHealthCheckFinished()
```

---

## Key Abstractions & Interfaces

### Cache Layer

| Interface | Implementation | File | Purpose |
|---|---|---|---|
| `IHybridCache<T>` | `DefaultHybridCache<T>` | `Common/Common/Source/Caching/` | Two-tier get/set/list with background refresh |
| `IListHybridCache<T>` | `DefaultHybridCache<T>` | `Common/Common/Source/Caching/` | List reconstruction from individual items |
| `IHybridCacheFactory` | `HybridCacheFactory` | `Common/Common/Source/Caching/` | Per-type singleton cache creation |
| `ICacheInvalidator` | `CacheInvalidator` | `Common/Common/Source/Caching/` | Cache mutation + 12 domain events |
| `IDistributedCache` | `CsvFileCache` / `WindowsRegistryCache` | `Common/Common/Source/Caching/` | L2 persistent backend |
| `ICachedItem` | Domain models | `Common/Common/Source/Caching/` | Contract for cacheable objects |

### Resource Resolution

| Interface | Implementation | File | Purpose |
|---|---|---|---|
| `IResourceResolver<T>` | `ResourceResolver<T>` | `Common/ResourceCaching/Source/` | Cache-first data access |
| `IResourceAggregator<T>` | `Update/Run/LocationAggregator` | `UpdateService/Core/Source/` | Multi-source data combining |
| `BaseResourceAggregator<T>` | (abstract) | `Common/ResourceCaching/Source/` | Aggregator base class |

### Business Logic

| Interface | Implementation | File | Purpose |
|---|---|---|---|
| `IUpdateResourceHandler` | `UpdateResourceHandler` | `UpdateService/Core/Source/` | Update CRUD + apply/prepare |
| `IUpdateRunResourceHandler` | `UpdateRunResourceHandler` | `UpdateService/Core/Source/` | Run lifecycle + rerun |
| `IUpdateLocationResourceHandler` | `UpdateLocationResourceHandler` | `UpdateService/Core/Source/` | Location + system health check |
| `IHealthCheckHandler` | `HealthCheckHandler` | `UpdateService/Core/Source/` | Pre-update validation via ECE |
| `IUpdatePreparationManager` | `UpdatePreparationManager` | `UpdateService/Core/Source/` | Multi-step workflow state machine |
| `IUpdateRunManager` | `UpdateRunManager` | `UpdateService/Core/Source/` | Run lifecycle management |
| `IUpdateDiscoveryManager` | `UpdateDiscoveryManager` | `UpdateService/Core/Source/` | Discovery orchestration |
| `ICancellationHandler` | `CancellationHandler` | `UpdateService/Core/Source/` | Graceful operation cancellation |

### External Clients

| Interface | Implementation | File | Purpose |
|---|---|---|---|
| `IEnterpriseCloudEngineClient` | `BufferedECEClient` | `EceClient/Source/` | ECE action plans, stamp info |
| `IDownloadServiceClient` | `DownloadServiceClient` | `DownloadServiceClient/Core/Source/` | DCAT scanning, package download |
| `IDrpClient` | `DrpClient` | `UpdateService/Core/Source/DrpClient/` | Platform consistency validation |
| `ILcmUpdateServiceClient` | `LcmUpdateServiceClient` | `LcmUpdateServiceClient/Source/` | Inter-node communication |
| `ICauClient` | `CauClient` | `DownloadServiceClient/Core/Source/` | Cluster-Aware Update |

### Security

| Interface | Implementation | File | Purpose |
|---|---|---|---|
| `ISignedXmlValidator` | `SignedXmlValidator` / `UnsignedXmlValidator` | `UpdateService/Security/Source/` | Discovery manifest validation |
| `ICertificateValidator` | `CertificateValidator` / `DefaultCertificateValidator` | `UpdateService/Security/Source/` | Microsoft root cert chain validation |
| `IArcTokenStore` | `ArcTokenStore` | `UpdateResourceProvider/Common/Source/` | Azure Arc machine identity tokens |

---

## Layer Interaction

```
┌─────────────────────────────────────────────────────────────────┐
│  Controllers (REST API Surface)                                  │
│  - ARM-compatible routes (/subscriptions/.../providers/...)      │
│  - OData query support ($filter, $select, $expand, $top, $skip) │
│  - mTLS certificate authentication                              │
│  - 11 controllers, 50+ endpoints                                │
│  - Async operations return 202 Accepted                          │
├─────────────────────────────────────────────────────────────────┤
│  Handlers + Managers (Business Logic)                            │
│  - ResourceHandlerBase<T> wraps ResourceResolver<T>              │
│  - UpdatePreparationManager: multi-step workflow state machine   │
│  - UpdateRunManager: run creation, progress, completion          │
│  - Uses CacheInvalidator for write-through cache updates         │
├─────────────────────────────────────────────────────────────────┤
│  ResourceResolver + Aggregators (Data Access)                    │
│  - Cache-first resolution via HybridCache                        │
│  - Aggregators combine ECE + discovery + providers               │
│  - Hash-based query isolation in cache keys (SHA256 → 16 hex)   │
│  - Configurable resolver timeout with stale-data fallback        │
├─────────────────────────────────────────────────────────────────┤
│  HybridCache (L1 Memory + L2 File)                               │
│  - L1: MemoryCache (100 MB, 120 min TTL)                        │
│  - L2: CsvFileCache on Cluster Shared Volume (24 hour TTL)      │
│  - Background refresh for stale items (5 min staleness threshold)│
│  - Item-list synchronization via CacheKeyMapping                 │
│  - Event-driven invalidation (12 domain events)                  │
│  - 9 serializer implementations selected by type                 │
├─────────────────────────────────────────────────────────────────┤
│  External Clients                                                │
│  - ECE: action plans, stamp info, package versions               │
│  - Download Service: DCAT scanning, package download             │
│  - DRP: platform consistency checks                              │
│  - LCM Client: inter-node communication (CN=URP cert)           │
├─────────────────────────────────────────────────────────────────┤
│  Infrastructure                                                  │
│  - Service Fabric (hosting, Reliable Collections, failover)      │
│  - ETW EventSource hierarchy (100+ event types, 11 sources)     │
│  - Unity DI container (wired in UnityConfig.RegisterComponents) │
│  - Cluster Shared Volumes (persistent file storage)              │
│  - Windows Registry (feature flags, alternative L2 cache)        │
└─────────────────────────────────────────────────────────────────┘
```

---

## Deployment Model & Runtime

### Service Fabric Hosting

The service runs as a **Service Fabric stateful service** using `OwinCommunicationListener` for HTTP. The generic `UpdateService<TDependency>` class receives three dependency interfaces at startup:

| Interface | Provides |
|---|---|
| `IConfigurationDependency` | Static configuration from Service Fabric settings XML |
| `IReliableStateDependency` | `IReliableStateManager` for Reliable Collections |
| `IServiceDiscoveryDependency` | Service Fabric service discovery |

### Service Lifecycle

```
1. Initialize()
   └─ Loads static config, peer certificates, creates Lazy<IBackgroundOperation> list

2. StartAsync()
   ├─ Initializes dynamic config from Reliable Collections
   ├─ Calls UnityConfig.RegisterComponents() → builds entire DI graph
   │   ├─ RegisterCaching() → L1 + L2 + HybridCache + Factory + Invalidator
   │   └─ RegisterServiceComponents() → all handlers, managers, clients
   ├─ Starts 4 background operations
   └─ Recovers interrupted preparation workflows (marks as failed)

3. Register()
   ├─ Configures OWIN HTTP pipeline
   │   ├─ JSON formatters (camelCase, custom converters)
   │   ├─ Custom controller/assembly/action resolvers
   │   ├─ Attribute-based routing via ResourceProviderDirectRouteProvider
   │   ├─ TracingHandler (position 0)
   │   ├─ Auth handler (position 1): mTLS or NoAuth
   │   └─ HttpMetricsHandler (position 0, outer)
   └─ Wires Unity container as DependencyResolver

4. StopAsync()
   ├─ Cancels running preparations via CancellationHandler
   └─ Disables all background operations
```

### Background Operations

| Operation | Interval | Purpose |
|---|---|---|
| `RefreshEceCacheOnStartup` | Once (5 min) | Prime ECE cache at startup, then self-disables |
| `RefreshEceCache` | Configurable | Periodic ECE state refresh (stamp info, versions, action plans) |
| `BackgroundUpdateDiscovery` | 1 hour | Discover new updates from all providers |
| `SystemBackgroundTelemetry` | 1 day | Emit system state telemetry (versions, OEM, run status) |

`StorageProviderCleanup` (6 hours, removes old packages) and `DefenderSignatureFileUpdate` (8 hours) exist but are currently disabled in favor of external runners.

### Storage

| Store | Technology | Data | Lifetime |
|---|---|---|---|
| **Reliable Collections** | Service Fabric `ReliableDictionary<string, string>` | Dynamic configuration, preparation workflow state | Replicated across nodes |
| **L1 Cache** | `MemoryCache` | Hot resource data | Process-scoped, 120 min TTL |
| **L2 Cache** | `CsvFileCache` on Cluster Shared Volume | Warm resource data | Shared across nodes, 24 hour TTL |
| **Feature Flags** | JSON file with eTag + SemaphoreSlim | Feature toggle state | File-based, persistent |
| **Installed Versions** | HybridCache + file | Component version tracking | 12 hour refresh |
| **Windows Registry** | `HKLM` | Alternative L2 cache, settings | Node-local, persistent |
| **Update Packages** | Cluster Shared Volume directories | Downloaded update content | Cleaned up after installation |

### Failover Behavior

As a Service Fabric stateful service, the **primary replica** handles all requests. On failover:

1. `StopAsync()` cancels in-progress preparations via `CancellationHandler`
2. Background operations are disabled
3. New primary's `StartAsync()` detects interrupted workflows and marks them as failed
4. L2 cache (Cluster Shared Volume) persists across failovers — the new primary can serve cached data immediately
5. L1 cache (in-memory) is cold — first requests after failover hit the aggregators

### Authentication & Security

| Layer | Mechanism |
|---|---|
| **API Authentication** | mTLS via `MtlsCertificateAuthorizationProvider` validating client cert subjects against allow-list |
| **Inter-node Communication** | Client certificate `CN=URP` via `LcmUpdateServiceClient` |
| **Manifest Validation** | XML digital signatures validated against Microsoft root certificate chain |
| **File Validation** | Authenticode verification via `WinVerifyTrust` P/Invoke |
| **Token Acquisition** | Azure Arc machine identity (two-stage auth via `IDENTITY_ENDPOINT` env var) |
| **Directory ACLs** | `DirectoryAccessControlHandler` restricts update directories to Administrators + service account |

### API Versioning

Supported versions: `2016-05-01`, `2018-10-01`, `2019-05-01`, `2020-10-01`, `2021-07-01`, `2022-03-01`, `2022-08-01` (current default). Version passed via `?api-version=` query parameter.

---

## Related Documentation

- [Feature Inventory](feature-inventory.md) — detailed breakdown of all 18 features
- [HybridCache Deep Dive](hybrid-cache-deep-dive.md) — DI registration, implementation layers, testing, knowledge graph
- [Caching System](cache/cache.md) — HybridCache design, L1/L2 architecture
- [Health Check](healthcheck/healthcheck.md) — environment validation system
- [Persistence](persistence/persistence.md) — data persistence layer design
