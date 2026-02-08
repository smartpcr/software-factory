## Key Abstractions & Interfaces

| Layer | Key Interfaces | Purpose |
|---|---|---|
| **Cache** | `IHybridCache<T>`, `IDistributedCache`, `ICacheInvalidator` | Two-tier caching with event-driven invalidation |
| **Resolution** | `IResourceResolver<T>`, `IResourceAggregator<T>` | Cache-first data access + multi-source combining |
| **Business Logic** | `IUpdateResourceHandler`, `IUpdateRunManager`, `IHealthCheckHandler`, `IUpdatePreparationManager` | Update CRUD, run lifecycle, health checks, workflow state machine |
| **External Clients** | `IEnterpriseCloudEngineClient`, `IDownloadServiceClient`, `ILcmUpdateServiceClient` | ECE action plans, package download, inter-node comms |
| **Security** | `ISignedXmlValidator`, `ICertificateValidator`, `IArcTokenStore` | Manifest validation, cert chains, Azure Arc identity |

---