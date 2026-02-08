## Deployment & Runtime

- **Hosting:** Service Fabric stateful service with OWIN/Web API 2.0
- **Storage:** Reliable Collections (replicated state), Cluster Shared Volumes (L2 cache + packages), Windows Registry (feature flags)
- **Background ops:** 4 timer-based operations (ECE cache startup prime, periodic ECE refresh, hourly discovery, daily telemetry)
- **Failover:** Primary replica handles requests; on failover, L2 cache (CSV) survives, L1 is cold, interrupted workflows marked failed
- **Security:** mTLS cert auth, XML signature validation (Microsoft root chain), Authenticode file verification, Azure Arc token acquisition
- **API versions:** `2016-05-01` through `2022-08-01` (current default)