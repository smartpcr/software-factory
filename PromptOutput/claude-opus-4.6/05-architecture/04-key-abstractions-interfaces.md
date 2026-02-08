## Key Abstractions & Interfaces

- **`IHybridCache<T>`** / `DefaultHybridCache<T>` — Two-tier cache (L1: MemoryCache 100MB/120min, L2: CsvFileCache on Cluster Shared Volume 24hr)
- **`IResourceResolver<T>`** — Cache-first data resolution
- **`IResourceAggregator<T>`** — Multi-source data combining (Update, UpdateRun, UpdateLocation aggregators)
- **`ICacheInvalidator`** — Write-through cache mutations + 12 event subscriptions
- **`IUpdateResourceHandler`** / `IUpdateRunResourceHandler` / `IHealthCheckHandler` — Business logic handlers
- **`IEnterpriseCloudEngineClient`** — ECE action plan execution
- **`IDownloadServiceClient`** — Package download (DCAT/CAU)