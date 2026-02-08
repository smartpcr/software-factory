Now let me search for feature-related files and examine key project structure:Let me examine key feature files and architecture:Now let me look at the main controllers and key files:Let me search for more key architectural patterns:Let me examine key core service structures and dependencies:Let me get more structural details on the key features:Now let me look at core update features and packages:Let me examine remaining core providers and create a comprehensive view:Perfect! Now let me create a comprehensive analysis. Let me check for one more key piece - the models:Feature-level map below; bullets use fragments to stay concise  

**Feature: API endpoints & ARM routing**  
- Summary: admin resource provider surface for update locations, updates, runs, and service utilities  
- Where: `src\UpdateResourceProvider\UpdateService\Controllers\Contract\*Controller.cs`, `Controllers\Source\*Implementation.cs`, `Common\SubscriptionService\Controllers`, `Common\ResourceProvider\Source\FabricOperationsController.cs`, `UpdateService\Service\UpdateServiceStartup.cs`  
- How: WebApi + ResourceRouting + UnauthorizedResourceControllerBase; controllers delegate to resource handlers/aggregators; mTLS auth handler injected in startup  
- Providers/Handlers/Strategies: `UpdateResourceControllerImplementation`, `UpdateLocationResourceControllerImplementation`, `UpdateRunResourceControllerImplementation`, `DiscoveryDiagnosticControllerImplementation`, `CacheControllerImplementation`, `UpdatePrepareControllerImplementation`, `DynamicConfigurationControllerImplementation`, `FeatureFlagControllerImplementation`, `DiscoveryConfigControllerImplementation`, `UrpOperationMetadataControllerImplementation`, `SubscriptionControllerImplementation`; handlers `UpdateResourceHandler`, `UpdateLocationResourceHandler`, `UpdateRunResourceHandler`, `DiscoveryDiagnosticInfoHandler`  
- Depends on: Update orchestration, Discovery/providers, Caching/persistence, Security, Telemetry  
- External packages: `Unity.WebApi`, `Microsoft.Data.OData`, `Microsoft.AzureStack.Common.Infrastructure.Framework`  
- Extension points: new controllers via ResourceRouting; new resource handlers via Unity registration  
- Endpoints (route template • method • handler • params • response • auth)  
  - `subscriptionssubresourceGroupsrgproviders/Microsoft.Update.Admin/updateLocationslocation • GET/GETALL from base • UpdateLocationResourceController • query `api-version`, OData • `UpdateLocation` ARM resource • mTLS  
  - `...updateLocationslocationcheckHealth` • POST op • UpdateLocationResourceController.CheckHealth • header `x-ms-client-request-id` • `IHttpActionResult` • mTLS  
  - `...updateLocationslocationupdates` • GET/GETALL • UpdateResourceController.GetAllResourcesByShortCut • query `api-version`, OData • list `Update` • mTLS  
  - `...updateLocationslocationupdatesupdate • PUT • UpdateResourceController.AddUpdate • body `UpdateSourceLocation`, query `skipSbeVersionValidation` • `IHttpActionResult` • mTLS  
  - `...updateLocationslocationupdatesupdateapply` • POST op • UpdateResourceController.ApplyUpdate • query `ignoreWarnings`, header `x-ms-client-request-id` • `IHttpActionResult` • mTLS  
  - `...updateLocationslocationupdatesupdateprepare` • POST op • UpdateResourceController.Prepare • header `x-ms-client-request-id` • `IHttpActionResult` • mTLS  
  - `...updateLocationslocationupdatesupdatecheckHealth` • POST op • UpdateResourceController.CheckHealth • `api-version` • `IHttpActionResult` • mTLS  
  - `...updateLocationslocationupdatesupdateupdateRuns` • GET/GETALL • UpdateRunResourceController.GetAllResourcesByShortCut • query `api-version`, OData • list `UpdateRun` • mTLS  
  - `...updateLocationslocationupdatesupdateupdateRunsrunrerun` • POST op • UpdateRunResourceController.Rerun • header `x-ms-client-request-id` • `IHttpActionResult` • mTLS  
  - `/providers/Microsoft.Update.Admin/operations` • GET • UrpOperationMetadataController.GetOperationsMetadata • query `api-version` • operations metadata doc • mTLS  
  - `/updatePrep` • GET • UpdatePrepareController.GetPrepareWorkflow • query `api-version` • `IHttpActionResult` • mTLS  
  - `/updatePrep` • DELETE • UpdatePrepareController.ClearPrepareWorkflow • query `updateRpIdString` • `IHttpActionResult` • mTLS  
  - `/dynamicConfiguration` • GET/POST • DynamicConfigurationController • body `DynamicConfiguration` • `DynamicConfigurationValues` or 200/400 • mTLS  
  - `/discoveryConfig` • GET/POST • DiscoveryConfigController • body `DiscoveryConfigParameter` • `DiscoveryConfiguration` or 200/400 • mTLS  
  - `/featureFlag` • GET/POST • FeatureFlagController • body `FeatureFlagParameter` • `FeatureFlagValues` or 200/404/500 • mTLS  
  - `/cachestypeName • GET/DELETE • CacheController.GetCachedItem/ClearCachedItems • query `cacheKey` • `IHttpActionResult` • mTLS  
  - `/cachestypeNamelist` • GET • CacheController.GetCachedItems • query `listCacheKey` • `IHttpActionResult` • mTLS  
  - `/caches/settings/exporttypeName • GET • CacheController.ExportCacheSettings • `IHttpActionResult` • mTLS  
  - `/caches/settings/importtypeName • POST • CacheController.ImportCacheSettings • body `CacheSettings` • `IHttpActionResult` • mTLS  
  - `/caches/stats` • GET • CacheController.ReportStats • `IHttpActionResult` • mTLS  
  - `/caches/packageVersions` • GET • CacheController.GetPackageVersions • `IHttpActionResult` • mTLS  
  - `/caches/setStampVersionstampVersion • PUT • CacheController.SetStampVersion • `IHttpActionResult` • mTLS  
  - `/caches/setSbeVersionsbeVersion • PUT • CacheController.SetSbeVersion • `IHttpActionResult` • mTLS  
  - `/subscriptionssubscriptionId • PUT • SubscriptionController.PutSubscription • `subscriptionId` path • `IHttpActionResult` • `[Authorize]`  
  - Mock endpoints: `EceServiceMock\EceMockController`, `DownloadServiceMock\MockDownloadServiceController` with explicit routes and typed responses  

**Feature: Update discovery & package providers**  
- Summary: discover update manifests and available update packages from local share and online URIs  
- Where: `UpdateService\Discovery\Source\*`, `UpdateService\Core\Source\DiscoveryManifestResolver.cs`, `ComponentDiscoveryManifestResolver.cs`, `StorageProvider\*Provider.cs`  
- How: URI resolution via `ConfigAwareUriResolver`; manifest resolution via `DiscoveryManifestResolver`; discovery via `LocalFolderDiscovery`, `SbeDiscovery`, `ComponentDiscovery`; providers aggregate into `UpdateResourceAggregator`  
- Providers/Handlers/Strategies: `LocalStorageProvider`, `OemUpdateProvider`, `ComponentUpdateProvider`; `DiscoveryManifestResolver`, `ComponentDiscoveryManifestResolver`; `LocalFolderDiscovery`, `SbeDiscovery`, `ComponentDiscovery`; `DiscoveryConfigAggregator`; `EnvironmentDiscoveryDependency`  
- Depends on: Configuration, Security (XML signature), Storage/persistence, Telemetry, External integrations  
- External packages: `Microsoft.AzureStack.Solution.Deploy.EnterpriseCloudEngine.Client`, `Newtonsoft.Json`  
- Extension points: implement `IUpdatePackageProvider`, `ILocalFolderDiscovery`, `ISbeDiscovery`, `IComponentDiscovery`, `IDiscoveryManifestResolver`, `IUriResolver` and register in Unity  

**Feature: Update preparation & execution orchestration**  
- Summary: prepare/apply updates, manage preparation workflows, and map preparation to update runs  
- Where: `UpdateService\Core\Source\UpdateResourceHandler.cs`, `UpdatePreparationManager.cs`, `UpdateRunManager.cs`, `UpdateRunAggregator.cs`, `UpdateStateIdentifier.cs`, `UpdatePrerequisitesManager.cs`, `UpdateTerminalStateValidator.cs`  
- How: `UpdateResourceHandler` validates update state, starts background `PrepareAndStart`, tracks progress via `UpdatePreparationManager` in reliable dictionaries, maps to `UpdateRun` via `UpdateRunManager`, drives ECE action plans through `UpdateRunAggregator`  
- Providers/Handlers/Strategies: `IUpdatePreparationManager`→`UpdatePreparationManager`, `IUpdateRunManager`→`UpdateRunManager`, `IUpdateStateIdentifier`→`UpdateStateIdentifier`, `IUpdatePrerequisitesManager`→`UpdatePrerequisitesManager`, `IUpdateTerminalStateValidator`→`UpdateTerminalStateValidator`, `IUpdateInstallationProgressCalculator`→`UpdateInstallationProgressCalculator`, `ICancellationHandler`→`CancellationHandler`  
- Depends on: Discovery/providers, Storage/persistence, Messaging/eventing, Telemetry, External integrations  
- External packages: `Microsoft.AzureStack.Common.Infrastructure.Framework`, `Microsoft.AzureStack.Common.Infrastructure.Telemetry`  
- Extension points: replace `IUpdatePreparationManager`, `IUpdateStateIdentifier`, `IUpdatePrerequisitesManager`, `IUpdateInstallationProgressCalculator` via Unity  

**Feature: Update content download & extraction**  
- Summary: download update packages (OS/SBE/solution) and extract/copy to share  
- Where: `UpdateService\Core\Source\DownloadHandler.cs`, `UpdatePackageExtractor.cs`, `DownloadConnector.cs`, `ApplicableUpdateFileResolver.cs`, `SolutionUpdateCopyManager.cs`  
- How: `DownloadHandler` orchestrates content download via `IDownloadConnector` + `IDownloadServiceClientManager`, `UpdatePackageExtractor` unpacks, `SolutionUpdateCopyManager` copies manifests/content to infra share  
- Providers/Handlers/Strategies: `IDownloadHandler`→`DownloadHandler`, `IDownloadConnector`→`DownloadConnector`, `IDownloadServiceClientManager`→`DownloadServiceClientManager`, `IPowerShellScriptHandler`→`PowerShellScriptHandler`, `IFileDownloader`→`HttpFileDownloader`, `IUpdatePackageExtractor`→`UpdatePackageExtractor`, `ISolutionUpdateCopyManager`→`SolutionUpdateCopyManager`  
- Depends on: Storage/persistence, Security (signatures), External integrations  
- External packages: `Microsoft.AzureStack.Download.DownloadService`, `System.Management.Automation_PowerShell_3.0.AzureStack`  
- Extension points: new `IDownloadConnector`, `IDownloadHandler`, `IFileDownloader` implementations  

**Feature: Health checks & environment readiness**  
- Summary: system and per‑update health checks before/after preparation  
- Where: `UpdateService\Core\Source\HealthCheckHandler.cs`, `UpdateLocationAggregator.cs`, `UpdateResourceAggregator.cs`  
- How: `HealthCheckHandler` executes readiness checks and exposes results; aggregators inject health state into Update/UpdateLocation  
- Providers/Handlers/Strategies: `IHealthCheckHandler`→`HealthCheckHandler`  
- Depends on: External integrations, Configuration, Telemetry  
- External packages: `Microsoft.EnvironmentReadiness.Validator`, `AzStackHci.EnvironmentChecker`  
- Extension points: replace `IHealthCheckHandler`  

**Feature: Storage / persistence & caching**  
- Summary: reliable dictionaries for state + hybrid cache for resource caching + file‑based stores  
- Where: `Common\ResourceProvider\DynamicConfigurationStore.cs`, `UpdateService\Core\Source\TransactionManager.cs`, `UpdateService\Service\UnityConfig.cs`, `Common\Common\Source\Caching\CsvFileCache.cs`, `FeatureFlagStore.cs`, `InstalledVersionStore.cs`  
- How: Service Fabric reliable dictionaries for prep/URP state; HybridCache + MemoryCache + CsvFileCache with CacheInvalidator; file stores for feature flags and installed versions  
- Providers/Handlers/Strategies: `DynamicConfigurationStore`, `TransactionManager`, `HybridCacheFactory`, `CacheInvalidator`, `CsvFileCache`, `FeatureFlagStore`, `InstalledVersionStore`  
- Depends on: Configuration, External integrations  
- External packages: `Microsoft.Extensions.Caching.Hybrid`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Caching.Abstractions`  
- Extension points: custom `IHybridCacheSerializerFactory`, `IDistributedCache`, `IFeatureFlagStore`, `IInstalledVersionStore`  

**Feature: Configuration**  
- Summary: static settings + dynamic config overlay + discovery configuration  
- Where: `UpdateService\Service\UrpConfiguration.cs`, `Common\ResourceProvider\ConfigurationHandler`, `DynamicConfigurationControllerImplementation.cs`, `DiscoveryConfigAggregator.cs`  
- How: `ConfigurationHandler` initialized from service config and dynamic store; `UrpConfiguration` reads typed values; dynamic config controller updates reliable dictionary and refreshes caches  
- Providers/Handlers/Strategies: `IDynamicConfigurationStore`→`DynamicConfigurationStore`, `IDiscoveryConfigAggregator`→`DiscoveryConfigAggregator`  
- Depends on: Storage/persistence, API endpoints  
- External packages: `Microsoft.AzureStack.Common.Infrastructure.Framework`  
- Extension points: custom `IDynamicConfigurationStore`, `IDiscoveryConfigAggregator`  

**Feature: Security**  
- Summary: mTLS request auth + signed XML validation for manifests/metadata  
- Where: `UpdateService\Service\UpdateServiceStartup.cs`, `UpdateService\Security\Source\*`, `Discovery\LocalFolderDiscovery.cs`  
- How: `MultiAuthMessageHandler` with `CertificateRequestAuthorizationProvider`; Signed XML validation uses `SignedXmlValidatorFactory` choosing `SignedXmlValidator` or `UnsignedXmlValidator`; certificate chain validation via `CertificateValidator`/`DefaultCertificateValidator`  
- Providers/Handlers/Strategies: `SignedXmlValidator`, `UnsignedXmlValidator`, `CertificateValidatorFactory`, `CertificateValidator`, `DefaultCertificateValidator`, `AutomaticUpdateUri`  
- Depends on: Configuration, Logging/Telemetry  
- External packages: `System.Security.Cryptography.Xml`, `System.Security.Cryptography.X509Certificates`  
- Extension points: implement `ISignedXmlValidator`, `ICertificateValidator`, custom auth handler  

**Feature: Messaging / eventing**  
- Summary: in‑process eventing for cache refresh and operation tracking  
- Where: `UpdateService\Common.Caching\CacheInvalidator`, `UpdateRunAggregator`, `UpdateLocationAggregator`, `UpdateResourceAggregator`  
- How: `CacheInvalidator` raises `OnUpdateAdded`, `OnRefreshUpdate`, `OnRefreshUpdates`, `OnFeatureFlagChanged`, `OnUpdateRunAdded`, `OnUpdateLocationSystemHealthCheckFinished`; aggregators subscribe to invalidate caches and trigger refresh flows  
- Providers/Handlers/Strategies: `CacheInvalidator` event model  
- Depends on: Storage/persistence, Telemetry  
- External packages: `Microsoft.Extensions.Caching.Hybrid`  
- Extension points: custom `ICacheInvalidator`  

**Feature: Logging / telemetry**  
- Summary: EventSource + telemetry reporter for metrics and diagnostics  
- Where: `UpdateService\Trace\*`, `Common\Trace\Source\*`, `EceClient\Trace\*`, `DownloadServiceClient\Trace\*`  
- How: `TelemetryEmitter` wraps `TelemetryReporter`; `UpdateServiceTelemetryEmitter` emits metrics; EventSources `UrpInfraEventSource`, `UrpTelemetryEventSource`, `CommonEventSource` log structured events  
- Providers/Handlers/Strategies: `ITelemetryEmitter`→`TelemetryEmitter`, EventSource loggers  
- Depends on: Messaging/eventing, Configuration  
- External packages: `Microsoft.AzureStack.Common.Infrastructure.Telemetry`, `EnterpriseLibrary.SemanticLogging`  
- Extension points: swap `ITelemetryEmitter`, add new EventSource  

**Feature: Scheduling / orchestration & background jobs**  
- Summary: periodic background operations for cache refresh, discovery, telemetry  
- Where: `UpdateService\Core\Source\BackgroundOperation\*`, `UpdateService\Service\UpdateService.cs`  
- How: `UpdateService.Initialize` registers `BackgroundOperation` instances; run on intervals; `RefreshEceCache`, `SystemBackgroundTelemetry`, `BackgroundUpdateDiscovery`, `RefreshEceCacheOnStartup`, `StorageProviderCleanup` (disabled)  
- Providers/Handlers/Strategies: `BackgroundOperation` subclasses listed above  
- Depends on: External integrations, Telemetry, Discovery/providers  
- External packages: none beyond core  
- Extension points: add new `IBackgroundOperation` and register in `UpdateService.Initialize`  

**Feature: OS update role execution**  
- Summary: orchestrates OS update download/install via PowerShell and DownloadService  
- Where: `UpdateResourceProvider\OsUpdateRole\Source\OsUpdateManager.cs`, `OsUpdateOperations.cs`, scripts under `OsUpdateRole\Scripts`  
- How: copies scripts to nodes, retrieves device info, downloads updates via DownloadService, installs via DISM, cleans up  
- Providers/Handlers/Strategies: `IOsUpdateManager`→`OsUpdateManager`, `IOsUpdateOperations`→`OsUpdateOperations`, `IPowerShellInvoker`  
- Depends on: External integrations, Storage/persistence, Security (credential use)  
- External packages: `Microsoft.AzureStack.Download.DownloadService`, `System.Management.Automation_PowerShell_3.0.AzureStack`  
- Extension points: custom `IOsUpdateOperations`  

**Feature: Tooling & release management**  
- Summary: PowerShell cmdlets and tools for packaging and discovery manifest management  
- Where: `UpdateResourceProvider\Tools\Source\*`, `ReleaseManagement\Update.ReleaseManagement\*.cs`  
- How: cmdlets manage discovery manifests, release rings, packaging hashes; `PackageCreator` builds metadata + hashes; `CentralDiscoveryManifestManager` injects entries  
- Providers/Handlers/Strategies: cmdlets `AddDiscoveryManifestEntryCommand`, `ExportDiscoveryManifestCommand`, `PackageCreator`, `CentralDiscoveryManifestManager`  
- Depends on: Discovery manifest formats, Security (signatures)  
- External packages: `System.Management.Automation_PowerShell_3.0.AzureStack`, `Nuget.Packaging*`  
- Extension points: add new cmdlets in Tools/ReleaseManagement  

**Feature: Standalone discovery & validation (ZTP)**  
- Summary: offline/online SBE discovery and validation utilities  
- Where: `StandaloneDiscovery\ZTPDiscovery\Source\SBEDiscovery.cs`, `StandaloneDiscovery\Validation\Source\SbeValidator.cs`  
- How: validate signed manifests, match package hash, model/sku, select latest SBE; compare against online discovery manifest  
- Providers/Handlers/Strategies: `SBEDiscovery`, `SbeValidator`, `ISignatureValidator`, `IDiscoveryManifestResolver`, `ILocalFolderDiscovery`, `ISbeDiscovery`  
- Depends on: Security, Discovery/providers  
- External packages: none beyond core  
- Extension points: custom `ISignatureValidator`, `IDiscoveryManifestResolver`  

**Feature: LCM PowerShell & update service client**  
- Summary: cmdlets and client wrappers to invoke URP/LCM APIs  
- Where: `LcmPowerShell\Source\*Cmdlet.cs`, `LcmUpdateServiceClient\Source\*`  
- How: cmdlets wrap `LcmUpdateServiceClient`, registry settings, and update operations; client resolves endpoints and executes HTTP  
- Providers/Handlers/Strategies: `LcmUpdateServiceClient`, `LcmUpdateServiceClientFactory`, cmdlet bases `UpdateOperationCmdletBase`  
- Depends on: API endpoints, Configuration  
- External packages: `System.Management.Automation_PowerShell_3.0.AzureStack`  
- Extension points: add new cmdlets or request executors  

**Feature: External integrations**  
- Summary: service talks to ECE, DownloadService, CAU, DRP, EnvironmentReadiness, Telemetry  
- Where: `EceClient\Source\*`, `DownloadServiceClient\Source\*`, `UpdateService\Service\UnityConfig.cs`, `UpdateService\Core\Source\InstalledVersionStore.cs`, `OsUpdateRole\Source\OsUpdateOperations.cs`  
- How: ECE for action plans and stamp info; DownloadService for scans/downloads; CAU for cluster update flows; DRP for deployment; EnvironmentReadiness for health checks; TelemetryReporter for metrics  
- Providers/Handlers/Strategies: `EnterpriseCloudEngineClient/BufferedECEClient`, `DownloadServiceClientManager`, `CauClient`, `DrpClient`, `TestResultClient`  
- Depends on: Configuration, Security  

**Feature dependency graph**  
`API endpoints` → `Update orchestration` → `Discovery/providers` → `Storage/persistence`  
`Update orchestration` → `Download/extraction` → `External integrations`  
`Background jobs` → `Discovery/providers` + `External integrations` + `Telemetry`  
`Security` → `Discovery/providers` + `API endpoints`  
`Configuration` → `Discovery/providers` + `Caching/persistence` + `Background jobs`  
`Telemetry/logging` ↔ `Messaging/eventing` ↔ `Caching/persistence`