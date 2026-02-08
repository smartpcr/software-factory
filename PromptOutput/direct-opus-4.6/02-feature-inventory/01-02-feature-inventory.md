# Feature Inventory

Comprehensive feature-level analysis of the Azure Stack HCI Update Resource Provider (URP).

---

## Table of Contents

1. [Hybrid Caching System](#1-hybrid-caching-system)
2. [Update Discovery](#2-update-discovery)
3. [Update Preparation & Installation Orchestration](#3-update-preparation--installation-orchestration)
4. [Resource Aggregation & Resolution](#4-resource-aggregation--resolution)
5. [Health Check System](#5-health-check-system)
6. [Security: XML Signature & Certificate Validation](#6-security-xml-signature--certificate-validation)
7. [Security: mTLS & Token Management](#7-security-mtls--token-management)
8. [Logging & Telemetry](#8-logging--telemetry)
9. [Configuration](#9-configuration)
10. [Background Jobs & Scheduling](#10-background-jobs--scheduling)
11. [API Endpoints & Routing](#11-api-endpoints--routing)
12. [External Integrations](#12-external-integrations)
13. [PowerShell Cmdlets](#13-powershell-cmdlets)
14. [Data Stores](#14-data-stores)
15. [OS Update Role](#15-os-update-role)
16. [Standalone Discovery (ZTP)](#16-standalone-discovery-ztp)
17. [Resource Trimming & Tags](#17-resource-trimming--tags)
18. [Eventing (In-Process)](#18-eventing-in-process)
19. [Feature Dependency Graph](#19-feature-dependency-graph)

---

## 1. Hybrid Caching System

**Summary:** Two-tier (L1 memory + L2 file/registry) caching with hash-based query isolation, background refresh, and event-driven invalidation.

### Where It Lives

- `src/Common/Common/Source/Caching/` — core cache interfaces and implementations
- `src/Common/Common/Source/Serialization/` — serializers for cache payloads
- `src/Common/Common/Source/Trace/CacheEventSource.cs` — cache-specific ETW events

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `IHybridCache<T>` | `Caching/IHybridCache.cs` | Primary cache interface (item get/set/remove) |
| `IListHybridCache<T>` | `Caching/IListHybridCache.cs` | List cache operations (reconstruct list from items) |
| `DefaultHybridCache<T>` | `Caching/DefaultHybridCache.cs` | L1+L2 composite implementation with hash-based sync |
| `CsvFileCache` | `Caching/CsvFileCache.cs` | L2 file-based `IDistributedCache` on cluster shared volumes |
| `WindowsRegistryCache` | `Caching/WindowsRegistryCache.cs` | L2 registry-based `IDistributedCache` |
| `CacheKey` (struct) | `Caching/CacheKey.cs` | Structured key with optional SHA256 hash suffix (16 hex chars) |
| `CacheKeyMapping` | `Caching/CacheKeyMapping.cs` | Bidirectional item-to-list mapping with hash isolation |
| `DefaultCacheKey` | `Caching/DefaultCacheKey.cs` | SHA256-based hash generation for query parameters |
| `HybridCacheFactory` | `Caching/HybridCacheFactory.cs` | Factory creating per-type `DefaultHybridCache` instances |
| `IHybridCacheFactory` | `Caching/IHybridCacheFactory.cs` | Factory interface |
| `CacheInvalidator` | `Caching/CacheInvalidator.cs` | Event-driven invalidation with 20+ domain events |
| `ICacheInvalidator` | `Caching/ICacheInvalidator.cs` | Invalidation interface |
| `ICachedItem` | `Caching/ICachedItem.cs` | Item contract (`GetCacheKey()`, `GetLastModifiedTime()`) |

### Configuration Classes

| Class | File | Properties |
|---|---|---|
| `CacheSettings` | `Caching/CacheSettings.cs` | Combines memory, file, and background refresh settings |
| `MemoryCacheSettings` | `Caching/MemoryCacheSettings.cs` | TTL, size limits, compaction percentage |
| `FileCacheSettings` | `Caching/FileCacheSettings.cs` | Cache folder path, TTL, purge expiration (days) |
| `BackgroundRefreshSettings` | `Caching/BackgroundRefreshSettings.cs` | IsEnabled, StalenessThreshold (5 min), ValueFactoryTimeout (3 min) |
| `CacheStatistics` | `Caching/CacheStatistics.cs` | Tier, EntryCount, EstimatedSize, TotalHits, TotalMisses |

### Serializers (9 implementations)

| Serializer | File | Format |
|---|---|---|
| `DefaultBinarySerializer<T>` | `Serialization/DefaultBinarySerializer.cs` | BinaryFormatter with KnownTypesBinder |
| `DefaultJsonSerializer<T>` | `Serialization/DefaultJsonSerializer.cs` | Newtonsoft.Json |
| `HybridJsonSerializer<T>` | `Serialization/HybridJsonSerializer.cs` | Newtonsoft.Json with custom settings |
| `HybridJsonTextSerializer<T>` | `Serialization/HybridJsonTextSerializer.cs` | System.Text.Json |
| `DefaultTextSerializer<T>` | `Serialization/DefaultTextSerializer.cs` | Text-based |
| `DefaultDataContractSerializer<T>` | `Serialization/DefaultDataContractSerializer.cs` | DataContractSerializer |
| `DefaultXElementSerializer<T>` | `Serialization/DefaultXElementSerializer.cs` | XElement-based |
| `MsgPackSerializer<T>` | `Serialization/MsgPackSerializer.cs` | MessagePack binary |
| `JsonTextSerializer<T>` | `Serialization/JsonTextSerializer.cs` | System.Text.Json variant |
| `SerializerFactory` | `Serialization/SerializerFactory.cs` | Type-dispatching factory (`SerializerType` enum) |

### Design Patterns

- **Two-Tier Caching:** L1 (IMemoryCache) for fast access, L2 (IDistributedCache) for persistence
- **Hash-Based Query Isolation:** Query parameters hashed to 16 hex chars; different queries maintain separate cache sets
- **Item-List Mapping:** Lists never stored directly; reconstructed from individually cached items via `CacheKeyMapping`
- **Background Refresh:** Proactive cache updates before expiration with configurable staleness threshold
- **Event-Driven Invalidation:** 20+ event types (OnUpdateAdded, OnRefreshUpdate, OnFeatureFlagChanged, etc.)
- **Factory Pattern:** `SerializerFactory` selects serializer by type and namespace

### Concurrency

- `ConcurrentDictionary` for cache key tracking
- `ReaderWriterLockSlim` for CsvFileCache statistics
- `SemaphoreSlim` for per-file locking in CsvFileCache
- `lock` for atomic multi-dictionary operations in CacheKeyMapping

### Dependencies

- `Microsoft.Extensions.Caching.Hybrid`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Caching.Abstractions`
- `Newtonsoft.Json`, `MessagePack`

---

## 2. Update Discovery

**Summary:** Discovers available updates from local folders, remote manifests (solution + component), and SBE/OEM packages via signed XML manifests.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Discovery/` — discovery providers and config
- `src/UpdateResourceProvider/UpdateService/Core/Source/` — discovery manager, manifest resolver

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `UpdateDiscoveryManager` | `Core/Source/UpdateDiscoveryManager.cs` | Orchestrates discovery across all providers |
| `ManifestDiscoveryManager` | `Core/Source/ManifestDiscoveryManager.cs` | Resolves and validates discovery manifests |
| `LocalFolderDiscovery` | `Discovery/Source/Local/LocalFolderDiscovery.cs` | Discovers updates from local directories |
| `ComponentDiscovery` | `Discovery/Source/Common/ComponentDiscovery.cs` | Solution + component update discovery |
| `SbeDiscovery` | `Discovery/Source/Common/SbeDiscovery.cs` | SBE/OEM update discovery |
| `ConfigAwareUriResolver` | `Discovery/Source/Common/ConfigAwareUriResolver.cs` | Resolves discovery URIs from configuration |
| `DiscoveryConfigAggregator` | `Discovery/Source/Common/DiscoveryConfigAggregator.cs` | Aggregates discovery configuration |
| `DiscoveryManifestResolver` | `Core/Source/DiscoveryManifestResolver.cs` | Resolves discovery manifests |
| `ComponentDiscoveryManifestResolver` | `Core/Source/ComponentDiscoveryManifestResolver.cs` | Component-specific manifest resolution |

### Storage Providers (Package Providers)

| Provider | File | Role |
|---|---|---|
| `ComponentUpdateProvider` | `Core/Source/StorageProvider/ComponentUpdateProvider.cs` | Automatic Microsoft component updates |
| `OemUpdateProvider` | `Core/Source/StorageProvider/OemUpdateProvider.cs` | Automatic OEM/SBE updates |
| `LocalStorageProvider` | `Core/Source/StorageProvider/LocalStorageProvider.cs` | Sideloaded updates from file shares |
| `UpdateProviderBase` | `Core/Source/StorageProvider/UpdateProviderBase.cs` | Base class for all providers |
| `AutomaticUpdateProviderBase` | `Core/Source/StorageProvider/AutomaticUpdateProviderBase.cs` | Base for auto-discovery providers |

### Factories

| Factory | File |
|---|---|
| `SbeDiscoveryFactory` | `Discovery/Source/Common/SbeDiscoveryFactory.cs` |
| `ComponentDiscoveryFactory` | `Discovery/Source/Common/ComponentDiscoveryFactory.cs` |
| `DiscoveryManifestFactory` | `Discovery/Source/Local/DiscoveryManifestFactory.cs` |
| `LocalFolderDiscoveryFactory` | `Discovery/Source/Local/LocalFolderDiscoveryFactory.cs` |

### Data Flow

```
Background Discovery Timer (1 hour)
    ↓
UpdateDiscoveryManager
    ├─ ComponentDiscovery → signed XML manifest → ComponentUpdateProvider
    ├─ SbeDiscovery → SBE manifest → OemUpdateProvider
    └─ LocalFolderDiscovery → local folder scan → LocalStorageProvider
    ↓
DiscoveryDiagnosticCache (tracks non-applicable updates + reasons)
    ↓
CacheInvalidator.OnRefreshUpdates → update cache
```

### Dependencies

- Feature 1 (Hybrid Caching), Feature 6 (XML Signature Validation), Feature 12 (External Integrations — ECE)

---

## 3. Update Preparation & Installation Orchestration

**Summary:** Multi-step workflow for downloading, extracting, validating, and installing updates via ECE action plans.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Core/Source/` — managers and workflow builders
- `src/UpdateResourceProvider/UpdateService/Models/Core/` — PreparationWorkflow, UpdateRun, Step

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `UpdatePreparationManager` | `Core/Source/UpdatePreparationManager.cs` | Preparation workflow state tracking with reliable collections |
| `PreparationWorkflowBuilder` | `Core/Source/PreparationWorkflowBuilder.cs` | Builds multi-step preparation workflows |
| `UpdateRunManager` | `Core/Source/UpdateRunManager.cs` | Update run lifecycle management |
| `DownloadHandler` | `Core/Source/DownloadHandler.cs` | Content downloading orchestration |
| `DownloadConnector` | `Core/Source/DownloadConnector/DownloadConnector.cs` | SBE download connector |
| `SolutionUpdateCopyManager` | `Core/Source/SolutionUpdateCopyManager.cs` | Solution update content management |
| `UpdatePackageExtractor` | `Core/Source/UpdatePackageExtractor.cs` | Package extraction |
| `AddUpdateManager` | `Core/Source/AddUpdateManager.cs` | Sideloaded update management |
| `TransactionManager` | `Core/Source/TransactionManager.cs` | Reliable collection state transactions |
| `CancellationHandler` | `Core/Source/Helpers/CancellationHandler.cs` | Update cancellation and cleanup |
| `UpdatePrerequisitesManager` | `Core/Source/Helpers/UpdatePrerequisitesManager.cs` | Prerequisite validation |
| `UpdateInstallationProgressCalculator` | `Core/Source/Helpers/UpdateInstallationProgressCalculator.cs` | Progress calculation from action plan state |
| `DownloadProgressTracker` | `Core/Source/StorageProvider/DownloadProgressTracker.cs` | Download progress reporting |

### Models

| Model | File |
|---|---|
| `PreparationWorkflow` | `Models/Core/PreparationWorkflow.cs` |
| `UpdateRun` | `Models/Core/UpdateRun.cs` |
| `Step` | `Models/Step.cs` |

### Workflow Steps

Download → Extract → HealthCheck → Complete (each step tracked with timestamps, status, error messages)

### Dependencies

- Feature 1 (Caching), Feature 5 (Health Check), Feature 12 (ECE Client), Feature 14 (Data Stores — Reliable Collections)

---

## 4. Resource Aggregation & Resolution

**Summary:** Aggregator pattern combining data from ECE, discovery providers, and cache into unified ARM-compatible resource views.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Core/Source/` — aggregators and resource handlers
- `src/Common/ResourceCaching/` — base aggregator, resource resolver

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `UpdateResourceAggregator` | `Core/Source/UpdateResourceAggregator.cs` | Combines update data from all providers |
| `UpdateRunAggregator` | `Core/Source/UpdateRunAggregator.cs` | Combines preparation + installation runs |
| `UpdateLocationAggregator` | `Core/Source/UpdateLocationAggregator.cs` | System update state aggregation |
| `DiscoveryDiagnosticInfoAggregator` | `Core/Source/DiscoveryDiagnosticInfoAggregator.cs` | Discovery diagnostics aggregation |
| `SolutionAdditionalDataAggregator` | `Core/Source/SolutionAdditionalDataAggregator.cs` | Additional solution metadata |
| `BaseResourceAggregator<T>` | `ResourceCaching/Source/BaseResourceAggregator.cs` | Abstract base for all aggregators |
| `IResourceAggregator<T>` | `ResourceCaching/Contract/IResourceAggregator.cs` | Aggregator interface |
| `ResourceResolver<T>` | `ResourceCaching/Source/ResourceResolver.cs` | Cache-first resolution with aggregator fallback |

### Resource Handlers

| Handler | File |
|---|---|
| `UpdateResourceHandler` | `Core/Source/UpdateResourceHandler.cs` |
| `UpdateRunResourceHandler` | `Core/Source/UpdateRunResourceHandler.cs` |
| `UpdateLocationResourceHandler` | `Core/Source/UpdateLocationResourceHandler.cs` |
| `DiscoveryDiagnosticInfoHandler` | `Core/Source/DiscoveryDiagnosticInfoHandler.cs` |

### Data Flow

```
API Request → ResourceResolver<T>
    ├─ Cache Hit → return cached data
    └─ Cache Miss → Aggregator
        → Combines ECE + Discovery + Providers + State
        → Cache result → return
```

### Dependencies

- Feature 1 (Caching), Feature 2 (Discovery), Feature 12 (ECE Client)

---

## 5. Health Check System

**Summary:** Pre-update environment validation via ECE action plans running 35+ validators across two validator engines.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Core/Source/HealthCheckHandler/`

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `HealthCheckHandler` | `HealthCheckHandler/HealthCheckHandler.cs` | Orchestrates health validation via ECE |
| `IHealthCheckHandler` | (Contract) | Interface for health check operations |
| `HealthCheckResultSource` | `HealthCheckHandler/HealthCheckResultSource.cs` | Source type enumeration |
| `HealthCheckConstants` | `HealthCheckHandler/HealthCheckConstants.cs` | Configuration constants |
| `HealthCheckSourceRuntimeParameterMap` | `HealthCheckHandler/HealthCheckSourceRuntimeParameterMap.cs` | Runtime parameter mapping |
| `ResultPropertyPruner` | `HealthCheckHandler/ResultPropertyPruner.cs` | Prunes verbose results for API responses |

### Validator Engines (Invoked via ECE Action Plans)

**CheckCloudHealth** — 25 validators from `Microsoft.EnvironmentReadiness.Validator`:
ClusterNodeStatusCheck, ClusterStatusCheck, StoragePools, StorageVolumes, StorageCSVs, InfraCapacity, InfraFreeIPs, MocExistence, MocUpdatePrecheck, SBEHealthCheck, TestCauSetup, and 14 more.

**EnvironmentValidatorPreUpdate** — 10 validators from `AzStackHci.EnvironmentChecker`:
DNS, Connectivity, External AD, Validated Recipe, Hardware, Network, Arc Integration, SBE Health, LLDP Validation, JIT validator.

### Dependencies

- Feature 3 (Orchestration — triggers health checks during preparation), Feature 12 (ECE Client)

---

## 6. Security: XML Signature & Certificate Validation

**Summary:** Validates signed XML discovery manifests and Authenticode signatures using Microsoft root certificate chain validation.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Security/`

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `ISignedXmlValidator` | `Security/Contract/ISignedXmlValidator.cs` | XML signature validation interface |
| `SignedXmlValidator` | `Security/Source/SignedXmlValidator.cs` | Production validator — strict certificate chain |
| `UnsignedXmlValidator` | `Security/Source/UnsignedXmlValidator.cs` | Test validator — allows all XML |
| `SignedXmlValidatorFactory` | `Security/Source/SignedXMLValidatorFactory.cs` | Selects validator based on assembly signature |
| `ICertificateValidator` | `Security/Source/ICertificateValidator.cs` | Certificate chain validation interface |
| `CertificateValidator` | `Security/Source/CertificateValidator.cs` | .NET Framework 4.72 — uses `CryptoApiWrapper` |
| `DefaultCertificateValidator` | `Security/Source/DefaultCertificateValidator.cs` | .NET 8.0 — uses regex for Microsoft Code Signing PCA |
| `CertificateValidatorFactory` | `Security/Source/CertificateValidatorFactory.cs` | Framework-aware factory |
| `CryptoApiWrapper` | `Security/Source/CryptoApiWrapper.cs` | Native P/Invoke: `Wintrust.dll`, `crypt32.dll` |
| `AuthenticodeUtils` | `Security/Source/AuthenticodeUtils.cs` | `WinVerifyTrust` for embedded exe signatures |
| `AutomaticUpdateUri` | `Security/Source/AutomaticUpdateUri.cs` | Selects prod vs test update URI based on signing |

### Security Event Logging

| Class | File | Target Framework |
|---|---|---|
| `ISignedXMLEventLogger` | `Security/Source/Logger/ISignedXMLEventLogger.cs` | Interface |
| `UrpSignedXMLEventLogger` | `Security/Source/Logger/UrpSignedXMLEventLogger.cs` | .NET Framework 4.72 |
| `SignedXMLEventLogger` | `Security/Source/Logger/SignedXMLEventLogger.cs` | .NET 8.0 |
| `SignedXMLEventLoggerFactory` | `Security/Source/Logger/SignedXMLEventLoggerFactory.cs` | Framework-aware factory |

### Validator Selection Logic

1. Assembly signed with test cert (`CN=msasci@microsoft.com`) → `UnsignedXmlValidator`
2. Assembly unsigned → `UnsignedXmlValidator`
3. Otherwise (production) → `SignedXmlValidator` (Microsoft certificates required)

### Dependencies

- Feature 2 (Discovery — validates discovery manifests)

---

## 7. Security: mTLS & Token Management

**Summary:** Mutual TLS certificate authentication for inter-service calls and Azure Arc machine identity token acquisition.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Service/Source/UpdateServiceStartup.cs` — auth pipeline setup
- `src/UpdateResourceProvider/Common/Source/ArcTokenStore.cs` — Arc token management
- `src/Common/ResourceProvider/Source/MessageHandlers/` — auth message handlers

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `ArcTokenStore` | `Common/Source/ArcTokenStore.cs` | Azure Arc machine identity token acquisition (two-stage auth) |
| `IArcTokenStore` | `Common/Source/Contract/IArcTokenStore.cs` | Token store interface |
| `AccessToken` | `Common/Source/AccessToken.cs` | Token data class |
| `ArcTokenAcquisitionException` | `Common/Source/ArcTokenAcquisitionException.cs` | Token error handling |
| `NoAuthMessageHandler` | `MessageHandlers/NoAuthMessageHandler.cs` | Test-only: authenticates all requests |
| `DirectoryAccessControlHandler` | `Core/Source/DirectoryAccessControlHandler.cs` | ACL management for directories |

### Auth Pipeline (configured in UpdateServiceStartup.cs)

1. `TracingHandler` (position 0) — HTTP tracing
2. Auth handler (position 1):
   - Production: `MultiAuthMessageHandler` → `MtlsCertificateAuthorizationProvider` + `CertificateRequestAuthorizationProvider`
   - Testing: `NoAuthMessageHandler` (when `disableAuth=true`)

### Arc Token Flow

1. Request to `IDENTITY_ENDPOINT` env var → 401 response
2. Extract secret file path from `WWW-Authenticate` header
3. Read secret from file, send with `Authorization: Basic <secret>`
4. Deserialize `access_token` from JSON response

### External Dependencies (from NuGet)

- `MtlsCertificateAuthorizationProvider`, `MultiAuthMessageHandler`, `CertificateRequestAuthorizationProvider`, `NoRevocationCheckCertificateChainPolicy` — from Azure Stack infrastructure packages

---

## 8. Logging & Telemetry

**Summary:** ETW (Event Tracing for Windows) EventSource hierarchy with static logger facades, OpenTelemetry-style metrics, and PII protection.

### Where It Lives

- `src/Common/Trace/Source/` — base event sources, metrics emitters
- `src/UpdateResourceProvider/UpdateService/Trace/` — URP-specific event sources
- `src/Common/Common/Source/Trace/` — cache event source

### EventSource Classes (11)

| EventSource Class | File | ETW Name |
|---|---|---|
| `EventSourceBase` | `Common/Trace/Source/EventSourceBase.cs` | Base with ETW keywords/tags/PII |
| `AdminRpEventSourceBase` | `Common/Trace/Source/AdminRpEventSourceBase.cs` | 1000+ lines of handler/aggregator events |
| `FrpInfraEventSource` | `Common/Trace/Source/FrpInfraEventSource.cs` | FRP infrastructure |
| `UpdateServiceTelemetryEventSource` | `Common/Trace/Source/UpdateServiceTelemetryEventSource.cs` | `Microsoft.AzureStack.UpdateService.Metric` |
| `UrpTelemetryEventSource` | `UpdateService/Trace/UrpTelemetryEventSource.cs` | `Microsoft-URP-TelemetryEventSource` |
| `UrpInfraEventSource` | `UpdateService/Trace/UrpInfraEventSource.cs` | URP infrastructure (extends AdminRpEventSourceBase) |
| `CommonEventSource` | `UpdateResourceProvider/Common/Trace/CommonEventSource.cs` | Common URP events |
| `CacheEventSource` | `Common/Common/Source/Trace/CacheEventSource.cs` | Cache hit/miss/get/set EventCounters |
| `EceClientEventSource` | `EceClient/Trace/EceClientEventSource.cs` | ECE client events |
| `DownloadServiceClientEventSource` | `DownloadServiceClient/Trace/DownloadServiceClientEventSource.cs` | Download service events |
| `OsUpdateEventSource` | `OsUpdateRole/Source/Trace/OsUpdateEventSource.cs` | OS update role events |

### Static Logger Facades (8)

| Logger | File |
|---|---|
| `UrpInfraEventLogger` | `UpdateService/Trace/UrpInfraEventLogger.cs` |
| `UrpTelemetryEventLogger` | `UpdateService/Trace/UrpTelemetryEventLogger.cs` |
| `FrpInfraEventLogger` | `Common/Trace/Source/FrpInfraEventLogger.cs` |
| `CacheEventLogger` | `Common/Common/Source/Trace/CacheEventLogger.cs` |
| `CommonEventLogger` | `UpdateResourceProvider/Common/Trace/CommonEventLogger.cs` |
| `EceClientEventLogger` | `EceClient/Trace/EceClientEventLogger.cs` |
| `DownloadServiceClientEventLogger` | `DownloadServiceClient/Trace/DownloadServiceClientEventLogger.cs` |
| `OsUpdateEventLogger` | `OsUpdateRole/Source/Trace/OsUpdateEventLogger.cs` |

### Telemetry Emitters

| Class | File | Role |
|---|---|---|
| `TelemetryEmitter` | `UpdateService/Trace/TelemetryEmitter.cs` | Main telemetry emitter using ITelemetryReporter; PII removal |
| `UpdateServiceTelemetryEmitter` | `Common/Trace/Source/UpdateServiceTelemetryEmitter.cs` | Static emitter with activity tracking, counters |
| `UpdateServiceMetric` | `Common/Trace/Source/UpdateServiceMetric.cs` | `[EventData]` metric with stopwatch timing |
| `HttpMetricsHandler` | `Common/Trace/Source/HttpMetricsHandler.cs` | DelegatingHandler for HTTP request/response metrics |
| `PreparationAttempt` | `UpdateService/Trace/PreparationAttempt.cs` | ITelemetryEvent for preparation tracking |

### Event ID Catalogs

| File | Range | Count |
|---|---|---|
| `UrpEventIds.cs` | 1000–9100 | 100+ events |
| `AdminRpEventIds.cs` | 1–1000+ | 100+ events |
| `CacheEventIds.cs` | Cache operations | 15+ events |
| `CommonEventIds.cs` | Common URP | ~20 events |
| `EceClientEventIds.cs` | ECE operations | ~20 events |
| `DownloadServiceClientEventIds.cs` | Download operations | ~15 events |
| `OsUpdateEventIds.cs` | OS update operations | ~10 events |

### PII Handling

- **EventTags:** `DropPii` (0x02000000), `HashPii` (0x04000000), `MarkPii` (0x08000000)
- **EventFieldTags:** `DropPiiField`, `HashPiiField`
- IP addresses, email addresses, and GUIDs sanitized in telemetry
- All events route to AEOTelemetry namespace (partner GUID: `4f50731a-89cf-4782-b3e0-dce8c90476ba`)

### Patterns

- Caller information: `[CallerFilePath]`, `[CallerMemberName]`, `[CallerLineNumber]`
- Correlation ID tracking via `DiagnosticContext.CorrelationRequestId`
- Self-describing ETW event format (`EtwSelfDescribingEventFormat`)

---

## 9. Configuration

**Summary:** Layered configuration from static IConfiguration, dynamic overrides in Reliable Collections, and type-specific cache settings.

### Where It Lives

- `src/Common/ResourceProvider/Source/` — ConfigurationHandler, DynamicConfigurationStore
- `src/UpdateResourceProvider/UpdateService/Service/Source/` — UrpConfiguration, UnityConfig

### Key Classes & Interfaces

| Class / Interface | File | Role |
|---|---|---|
| `ConfigurationHandler` | `ResourceProvider/Source/Handlers/ConfigurationHandler.cs` | Singleton; static IConfiguration + dynamic overrides |
| `DynamicConfigurationStore` | `ResourceProvider/Source/DynamicConfigurationStore.cs` | Reliable Dictionary storage for runtime config |
| `ConfigurationValueParser` | `ResourceProvider/Source/ConfigurationValueParser.cs` | Static parsing with defaults (time, bool, int) |
| `UrpConfiguration` | `Service/Source/UrpConfiguration.cs` | Service-specific settings with defaults |
| `UnityConfig` | `Service/Source/UnityConfig.cs` | Unity DI container bootstrap |

### UrpConfiguration Defaults

| Setting | Default |
|---|---|
| `CreateActionPlanInstanceRetryDelay` | 1 minute |
| `CreateActionPlanInstanceTimeout` | 2 hours |
| `HealthCheckExpirationDuration` | 3 hours |
| `HealthCheckActionPlanMaxWaitDuration` | 2.5 hours |
| `PackageDownloadRetryInterval` | 180 seconds |
| `DownloadRetryAttemptLimit` | 10 retries |

### Configuration Sources (Priority Order)

1. Dynamic overrides from Reliable Collections (highest)
2. Static `IConfiguration` from Service Fabric settings (lowest)

### Dependencies

- Feature 14 (Data Stores — Reliable Collections for DynamicConfigurationStore)

---

## 10. Background Jobs & Scheduling

**Summary:** Timer-based periodic operations for discovery, cache refresh, cleanup, and telemetry using `System.Timers.Timer`.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Core/Source/BackgroundOperation/`

### Key Classes

| Class | File | Interval | Purpose |
|---|---|---|---|
| `BackgroundOperation` (abstract) | `BackgroundOperation.cs` | Configurable | Base class with `System.Timers.Timer` |
| `BackgroundUpdateDiscovery` | `BackgroundUpdateDiscovery.cs` | 1 hour | Discovers and re-prepares obsolete updates |
| `RefreshEceCache` | `RefreshEceCache.cs` | Configurable | Refreshes ECE cache (stamp info, versions, action plans) |
| `RefreshEceCacheOnStartup` | `RefreshEceCacheOnStartup.cs` | 5 min (once) | One-time ECE cache refresh at startup, then self-disables |
| `StorageProviderCleanup` | `StorageProviderCleanup.cs` | 6 hours | Removes packages of installed/recalled/obsolete updates |
| `DefenderSignatureFileUpdate` | `DefenderSignatureFileUpdate.cs` | 8 hours | Downloads Defender signature files |
| `SystemBackgroundTelemetry` | `SystemBackgroundTelemetry.cs` | 1 day | Emits system state telemetry (versions, OEM, run status) |

### Interface

- `IBackgroundOperation` — `Enabled` property, `Initialize(UnityContainer)` method

### Service Lifecycle Integration

Background operations are initialized as `Lazy<IBackgroundOperation>` in `UpdateService<TDependency>.Initialize()`, started in `StartAsync()`, and stopped in `StopAsync()`.

### Dependencies

- Feature 2 (Discovery), Feature 1 (Caching), Feature 12 (ECE Client), Feature 8 (Telemetry)

---

## 11. API Endpoints & Routing

**Summary:** ARM-compatible REST API with ASP.NET Web API 2.0, OData query support, attribute-based routing, and MTLS authentication.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Controllers/Contract/` — controller interfaces
- `src/UpdateResourceProvider/UpdateService/Controllers/Source/` — implementations

### Framework

- ASP.NET Web API 2.0 with OData support
- Service Fabric OwinCommunicationListener
- Attribute-based routing with custom `ResourceProviderDirectRouteProvider`

### API Versioning

Supported: `2016-05-01`, `2018-10-01`, `2019-05-01`, `2020-10-01`, `2021-07-01`, `2022-03-01`, `2022-08-01` (current default)

### Controllers

#### Resource Controllers (ARM-style nested resources)

**UpdateLocationResourceController** — `/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Update.Admin/updateLocations`

| Method | Route | Description |
|---|---|---|
| GET | `/updateLocations` | List update locations (OData support) |
| GET | `/updateLocations/{name}` | Get specific update location |
| POST | `/updateLocations/{name}/checkHealth` | Start system health check (202 Accepted) |

**UpdateResourceController** — `.../updateLocations/{loc}/updates`

| Method | Route | Description |
|---|---|---|
| GET | `/updates` | List updates (OData, superseded filter, nocache) |
| GET | `/updates/{name}` | Get specific update |
| PUT | `/updates/{name}` | Add sideloaded update from file share |
| POST | `/updates/{name}/apply` | Apply/install update (202 Accepted) |
| POST | `/updates/{name}/prepare` | Prepare update (202 Accepted) |
| POST | `/updates/{name}/checkHealth` | Health check for specific update |

**UpdateRunResourceController** — `.../updates/{upd}/updateRuns`

| Method | Route | Description |
|---|---|---|
| GET | `/updateRuns` | List update runs (OData, maxDepth) |
| GET | `/updateRuns/{name}` | Get specific update run |
| POST | `/updateRuns/{name}/rerun` | Rerun from latest failure point (202 Accepted) |

**DiscoveryDiagnosticController** — `.../providers/Microsoft.Update.Admin/discoveryDiagnostics`

| Method | Route | Description |
|---|---|---|
| GET | `/discoveryDiagnostics` | Get discovery diagnostic info |

#### Administrative Controllers

**CacheController** (`/caches`)

| Method | Route | Description |
|---|---|---|
| DELETE | `/{typeName}` | Clear cache by type |
| GET | `/{typeName}/list` | List cached items |
| GET | `/{typeName}` | Get single cached item |
| GET | `/settings/export/{typeName}` | Export cache settings |
| POST | `/settings/import/{typeName}` | Import cache settings |
| GET | `/stats` | Cache statistics |
| GET | `/packageVersions` | Package versions from ECE |
| PUT | `/setStampVersion/{version}` | Set stamp version |
| PUT | `/setSbeVersion/{version}` | Set SBE version |

**DynamicConfigurationController** (`/dynamicConfiguration`)

| Method | Route | Description |
|---|---|---|
| POST | `/` | Set dynamic config key/value |
| GET | `/` | Get all dynamic configs |

**DiscoveryConfigController** (`/discoveryConfig`)

| Method | Route | Description |
|---|---|---|
| POST | `/` | Set update ring or override URI |
| GET | `/` | Get current discovery config |

**FeatureFlagController** (`/featureFlag`)

| Method | Route | Description |
|---|---|---|
| POST | `/` | Set feature flag status |
| GET | `/` | Get all feature flags |

**UpdatePrepareController** (`/updatePrep`)

| Method | Route | Description |
|---|---|---|
| GET | `/` | Get all preparation workflows |
| DELETE | `/` | Clear preparation workflow |

**UrpOperationMetadataController** (`/providers/Microsoft.Update.Admin/operations`)

| Method | Route | Description |
|---|---|---|
| GET | `/operations` | Get ARM operation metadata |

**SubscriptionController** (`/subscriptions`)

| Method | Route | Description |
|---|---|---|
| PUT | `/{subscriptionId}` | Register subscription [Authorize] |

### Common Response Patterns

- **200 OK** — synchronous success
- **202 Accepted** — async operations (apply, prepare, checkHealth, rerun)
- **400 Bad Request** — validation errors
- **404 Not Found** — resource not found

### Dependencies

- Feature 7 (mTLS Auth), Feature 4 (Aggregation), Feature 1 (Caching)

---

## 12. External Integrations

**Summary:** REST clients for ECE, Download Service, DRP, and inter-node LCM communication with retry policies and certificate auth.

### Where It Lives

- `src/EceClient/` — ECE client
- `src/DownloadServiceClient/` — Download Service + CAU client
- `src/UpdateResourceProvider/UpdateService/Core/Source/DrpClient/` — DRP client
- `src/UpdateResourceProvider/LcmUpdateServiceClient/` — Inter-node client

### ECE (Environment Configuration Engine) Client

| Class | File | Role |
|---|---|---|
| `EnterpriseCloudEngineClient` | `EceClient/Source/EnterpriseCloudEngineClient.cs` | Primary ECE REST client |
| `BufferedECEClient` | `EceClient/Source/BufferedECEClient.cs` | Caching layer for ECE |
| `EnterpriseCloudEngineReliableClient` | `EceClient/Source/EnterpriseCloudEngineReliableClient.cs` | Reliable wrapper with retry |
| `ECEClientCustomErrorRetryPolicy` | `EceClient/Source/ECEClientCustomErrorRetryPolicy.cs` | Retries on 409 Conflict, 423 Locked |

**Key ECE APIs:** `GetCloudParameters()`, `GetStampInformation()`, `Get/SetStampVersion()`, `Get/SetOemVersion()`, `GetOemPublisher()`, `GetOemFamily()`, `CreateActionPlanInstance()`, `GetActionPlanInstance()`, `GetPackageVersions()`

### Download Service Client

| Class | File | Role |
|---|---|---|
| `DownloadServiceClient` | `DownloadServiceClient/Core/Source/DownloadServiceClient.cs` | Download service REST client |
| `DownloadServiceClientManager` | `DownloadServiceClient/Core/Source/DownloadServiceClientManager.cs` | Lifecycle management |
| `DownloadServiceClientFactory` | `DownloadServiceClient/Core/Source/DownloadServiceClientFactory.cs` | Factory |
| `CauClient` | `DownloadServiceClient/Core/Source/CauClient.cs` | Cluster-Aware Update client |

**Key APIs:** `ScanPlatformUpdate()`, `DownloadPlatformUpdate()`, `DownloadHttpContent()` (endpoints: `udi/scan`, `udi/download`, `http/download`, API version `2023-01-01`)

### DRP (Deployment Resource Provider) Client

| Class | File | Role |
|---|---|---|
| `DrpClient` | `Core/Source/DrpClient/DrpClient.cs` | Platform consistency validation |
| `IDrpClient` | `Core/Contract/IDrpClient.cs` | Interface |

**Key API:** `CheckPlatformUpdateConditions()` (POST to `{baseUri}/checkPlatformUpdateConditions`, default: `https://deploymentprovider.azs:8443/api/`)

### LCM Update Service Client (Inter-Node)

| Class | File | Role |
|---|---|---|
| `LcmUpdateServiceClient` | `LcmUpdateServiceClient/Source/LcmUpdateServiceClient.cs` | Inter-node REST client |
| `LcmUpdateServiceClientFactory` | `LcmUpdateServiceClient/Source/LcmUpdateServiceClientFactory.cs` | Factory with Failover Cluster discovery |

**Key APIs:** `GetUpdates()`, `GetUpdate()`, `GetUpdateLocation()`, `GetUpdateRuns()`, `ApplyUpdate()`, `PrepareUpdate()`, `CheckUpdateHealth()`, `ResumeUpdateRun()`

**Authentication:** Client certificate `CN=URP`

### HTTP Infrastructure

| Class | File | Role |
|---|---|---|
| `HttpClientSingleton` | `Core/Source/Helpers/HttpClientSingleton.cs` | Thread-safe singleton HTTP client |
| `HttpFileDownloader` | `Core/Source/HttpFileDownloader.cs` | Range-based file download with Azure Storage headers |

---

## 13. PowerShell Cmdlets

**Summary:** 15+ PowerShell cmdlets for local cluster management wrapping `LcmUpdateServiceClient`.

### Where It Lives

- `src/UpdateResourceProvider/LcmPowerShell/Source/`

### Key Cmdlets

| Cmdlet | File | Purpose |
|---|---|---|
| `NewSolutionUpdateClientCmdlet` | `NewSolutionUpdateClientCmdlet.cs` | Creates LCM client instance |
| `GetSolutionUpdateCmdlet` | `GetSolutionUpdateCmdlet.cs` | List/get available updates |
| `AddSolutionUpdateCmdlet` | `AddSolutionUpdateCmdlet.cs` | Add sideloaded update |
| `StartSolutionUpdateCmdlet` | `StartSolutionUpdateCmdlet.cs` | Start update installation |
| + 11 more cmdlets | Various | Other operations |

### Base Classes

| Base Class | File | Role |
|---|---|---|
| `LcmClientCmdletBase` | `LcmClientCmdletBase.cs` | Base for all LCM cmdlets |
| `UpdateLocationDependentCmdlet` | Various | Update location-aware base |
| `ClusterNodeCmdletBase` | Various | Cluster node-aware base |
| `UpdateOperationCmdletBase` | Various | Update operation base |

### Supporting Classes

| Class | File | Role |
|---|---|---|
| `RemoteRegistryOperations` | `RemoteRegistryOperations.cs` | Remote registry access for cluster config |
| `RegistrySettingConfig` | `RegistrySettingConfig.cs` | Registry setting configuration |
| `UpdateSettingConfiguration` | `UpdateSettingConfiguration.cs` | Update setting config |
| `UpdateLocationCache` | `UpdateLocationCache.cs` | Client-side update location cache |

### Dependencies

- Feature 12 (LCM Update Service Client)

---

## 14. Data Stores

**Summary:** Multiple persistence backends including Service Fabric Reliable Collections, file-based JSON stores, registry, and in-memory caches.

### Where It Lives

- `src/Common/ResourceProvider/Source/` — ResourceProviderStore, DynamicConfigurationStore
- `src/UpdateResourceProvider/UpdateService/Core/Source/` — FeatureFlagStore, InstalledVersionStore
- `src/UpdateResourceProvider/Common/Source/` — ArcTokenStore, PublicUriStore

### Store Implementations

| Store | File | Backend | Purpose |
|---|---|---|---|
| `ResourceProviderStore` | `ResourceProvider/Source/ResourceProviderStore.cs` | ARM | Route information storage |
| `DynamicConfigurationStore` | `ResourceProvider/Source/DynamicConfigurationStore.cs` | Reliable Dictionary | Runtime configuration |
| `FeatureFlagStore` | `Core/Source/FeatureFlag/FeatureFlagStore.cs` | JSON file with eTag + SemaphoreSlim | Feature flag persistence |
| `InstalledVersionStore` | `Core/Source/InstalledVersions/InstalledVersionStore.cs` | HybridCache + file (12h expiry) | Installed version tracking |
| `ArcTokenStore` | `Common/Source/ArcTokenStore.cs` | Azure Arc identity endpoint | Token acquisition |
| `PublicUriStore` | `Common/Source/PublicUriStore.cs` | Various | Public URI management |
| `DiscoveryDiagnosticCache` | `Core/Source/DiscoveryDiagnosticCache.cs` | ConcurrentDictionary (in-memory) | Discovery diagnostic tracking |

---

## 15. OS Update Role

**Summary:** Service Fabric stateful service for OS-level update operations with its own event source and retry infrastructure.

### Where It Lives

- `src/UpdateResourceProvider/OsUpdateRole/Source/`

### Key Classes

| Class | File | Role |
|---|---|---|
| `OsUpdateEventSource` | `Trace/OsUpdateEventSource.cs` | ETW events for OS updates |
| `OsUpdateEventLogger` | `Trace/OsUpdateEventLogger.cs` | Static logger facade |
| `Retryer` | `Retryer.cs` | Custom retry with configurable max retries, delay, disallowed/ignored exception types |

---

## 16. Standalone Discovery (ZTP)

**Summary:** Zero-touch provisioning discovery for SBE updates with rolling file logging for standalone operation.

### Where It Lives

- `src/UpdateResourceProvider/StandaloneDiscovery/`

### Key Classes

| Class | File | Role |
|---|---|---|
| `SBEDiscovery` | `ZTPDiscovery/Source/SBEDiscovery.cs` | Standalone SBE discovery |
| `RollingFileEventListener` | `Common/Source/RollingFileEventListener.cs` | File-based EventListener with rolling files |
| `IStandaloneDisoveryLogger` | `ZTPDiscovery/Source/Logger/IStandaloneDisoveryLogger.cs` | Logger interface |
| `Logger` | `ZTPDiscovery/Source/Logger/Logger.cs` | Logger implementation |

---

## 17. Resource Trimming & Tags

**Summary:** ARM resource filtering based on query context, removing unnecessary properties from API responses.

### Where It Lives

- `src/UpdateResourceProvider/UpdateService/Core/Source/Trimmers/`

### Key Classes

| Trimmer | File | Resource Type |
|---|---|---|
| `UpdateTrimmer` | `Trimmers/UpdateTrimmer.cs` | Update |
| `UpdateRunTrimmer` | `Trimmers/UpdateRunTrimmer.cs` | UpdateRun |
| `UpdateLocationTrimmer` | `Trimmers/UpdateLocationTrimmer.cs` | UpdateLocation |

---

## 18. Eventing (In-Process)

**Summary:** C# `event EventHandler<T>` pattern for decoupled cache invalidation and state change propagation.

### Where It Lives

- `src/Common/Common/Source/Caching/ICacheInvalidator.cs` — event definitions
- `src/Common/Common/Source/Caching/CacheInvalidator.cs` — event raising

### Event Types (20+)

| Event | EventArgs Type | Trigger |
|---|---|---|
| `OnUpdateAdded` | `UpdateAddedEventArgs` | New update discovered/added |
| `OnRefreshUpdate` | `RefreshUpdateEventArgs` | Single update refresh |
| `OnRefreshUpdates` | `RefreshUpdatesEventArgs` | Bulk update refresh |
| `OnUpdateRunAdded` | `UpdateRunAddedEventArgs` | New update run created |
| `OnRefreshUpdateRun` | `RefreshUpdateRunEventArgs` | Update run refresh |
| `OnRefreshActionPlanInstance` | `RefreshActionPlanInstanceEventArgs` | Action plan refresh |
| `OnRefreshUpdateActionPlanInstances` | `RefreshUpdateActionPlanInstancesEventArgs` | Bulk action plan refresh |
| `OnMonitorUpdateInstallationActionPlanInstance` | `MonitorUpdateInstallationActionPlanInstanceEventArgs` | Installation monitoring |
| `OnUpdateLocationSystemHealthCheckFinished` | `UpdateLocationHealthCheckFinishedEventArgs` | Health check completed |
| `OnRefreshCloudParametersXml` | `RefreshCloudParametersXmlEventArgs` | Cloud params refresh |
| `OnRefreshDiscoveryConfig` | `RefreshDiscoveryConfigEventArgs` | Discovery config change |
| `OnFeatureFlagChanged` | `FeatureFlagChangedEventArgs` | Feature flag toggled |

---

## 19. Feature Dependency Graph

```
                    ┌─────────────────────┐
                    │  11. API Endpoints   │
                    └──────┬──────────────┘
                           │
              ┌────────────┼────────────────┐
              ▼            ▼                ▼
    ┌─────────────┐  ┌──────────┐  ┌──────────────┐
    │ 7. mTLS/Auth│  │4. Aggreg.│  │17. Trimming  │
    └─────────────┘  └────┬─────┘  └──────────────┘
                          │
           ┌──────────────┼──────────────┐
           ▼              ▼              ▼
    ┌────────────┐  ┌──────────┐  ┌────────────┐
    │2. Discovery│  │1. Caching│  │3. Orchestr. │
    └─────┬──────┘  └────┬─────┘  └──────┬─────┘
          │              │               │
          ▼              ▼               ▼
    ┌──────────┐  ┌────────────┐  ┌──────────┐
    │6. XML Sec│  │18. Eventing│  │5. Health  │
    └──────────┘  └────────────┘  └──────────┘
                                       │
          ┌────────────────────────────┘
          ▼
    ┌──────────────┐      ┌──────────────┐
    │12. Ext Integr│◄────►│14. Data Store│
    │  (ECE, DRP,  │      │  (Reliable   │
    │  Download)   │      │  Collections)│
    └──────┬───────┘      └──────────────┘
           │
    ┌──────┴───────┐
    │13. PowerShell│
    └──────────────┘

Cross-cutting concerns (connected to most features):
  - 8. Logging & Telemetry
  - 9. Configuration
  - 10. Background Jobs
  - 15. OS Update Role
  - 16. Standalone Discovery (ZTP)
```

### Dependency Summary

| Feature | Depends On |
|---|---|
| 1. Hybrid Caching | 18 (Eventing) |
| 2. Update Discovery | 1, 6, 12 |
| 3. Orchestration | 1, 5, 12, 14 |
| 4. Aggregation | 1, 2, 12 |
| 5. Health Check | 12 (ECE) |
| 6. XML Security | — (leaf) |
| 7. mTLS/Auth | — (leaf) |
| 8. Logging | — (cross-cutting) |
| 9. Configuration | 14 (Reliable Collections) |
| 10. Background Jobs | 1, 2, 8, 12 |
| 11. API Endpoints | 4, 7, 17 |
| 12. External Integrations | — (leaf) |
| 13. PowerShell | 12 (LCM Client) |
| 14. Data Stores | — (leaf) |
| 15. OS Update Role | — (standalone) |
| 16. ZTP Discovery | — (standalone) |
| 17. Trimming | — (leaf) |
| 18. Eventing | — (leaf) |

---

## NuGet Package Dependencies

| Package | Used By |
|---|---|
| `Microsoft.Extensions.Caching.Hybrid` | Caching (L1+L2 core) |
| `Microsoft.Extensions.Caching.Memory` | Caching (L1 memory) |
| `Microsoft.Extensions.Caching.Abstractions` | Caching (interfaces) |
| `Newtonsoft.Json` | Serialization, all REST clients |
| `MessagePack` | MsgPackSerializer |
| `Unity` / `Unity.Abstractions` | Dependency injection |
| `ValueInjecter` | Object-to-object mapping |
| `System.Web.OData` | OData query support in API |
| `System.Management.Automation` | PowerShell cmdlets |
| `Microsoft.Diagnostics.Tracing.TraceEvent` | ETW event collection (tests) |
| `Microsoft.AzureStack.Common.Infrastructure.Framework` | HTTP client, service discovery, reliable operations |
| `Microsoft.AzureStack.Common.Infrastructure.Telemetry.TelemetryReporter` | Geneva/MDSD telemetry |
| `Microsoft.AzureStack.DeploymentProvider.Contracts.UrpEndpoint` | DRP endpoint contracts |
| `Microsoft.AzureStack.Download.UpdateToolsLib` | UDI API support |
