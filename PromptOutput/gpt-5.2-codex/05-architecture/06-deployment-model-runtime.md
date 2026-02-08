## Deployment Model & Runtime

- **Hosting**: Service Fabric stateful service with OWIN/Web API 2.0 + Unity DI
- **Primary replica** handles all requests; on failover, `StopAsync()` cancels in-progress work, new primary recovers and marks interrupted workflows as failed
- **Storage**: Reliable Collections (replicated state), L2 cache on Cluster Shared Volumes (survives failover), Windows Registry (feature flags), in-memory L1 cache (cold after failover)
- **Background ops**: 4 active timer-based operations (ECE startup prime, ECE periodic refresh, discovery every 1h, telemetry every 24h)
- **Auth**: mTLS cert-based API auth, XML digital signature validation for manifests, Azure Arc machine identity tokens
- **API versions**: 8 versions from `2016-05-01` through `2022-08-01`