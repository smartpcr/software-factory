## 7. Messaging & Event-Driven Behavior

### 7.1 Events Published by CacheInvalidator

**File**: `CacheInvalidator.cs`

| Event | Args Type | Trigger |
|-------|----------|---------|
| `OnUpdateLocationSystemHealthCheckFinished` | `UpdateLocationHealthCheckFinishedEventArgs` | Health check completes |
| `OnUpdateAdded` | `UpdateAddedEventArgs` | New update discovered |
| `OnRefreshUpdate` | `RefreshUpdateEventArgs` | Update needs refresh |
| `OnRefreshUpdates` | `RefreshUpdatesEventArgs` | All updates need refresh |
| `OnUpdateRunAdded` | `UpdateRunAddedEventArgs` | New update run created |
| `OnRefreshUpdateRun` | `RefreshUpdateRunEventArgs` | Update run needs refresh |
| `OnRefreshActionPlanInstance` | `RefreshActionPlanInstanceEventArgs` | Action plan needs refresh |
| `OnRefreshUpdateActionPlanInstances` | `RefreshUpdateActionPlanInstancesEventArgs` | All action plans need refresh |
| `OnMonitorUpdateInstallationActionPlanInstance` | `MonitorUpdateInstallationActionPlanInstanceEventArgs` | Installation monitoring |
| `OnRefreshCloudParametersXml` | `RefreshCloudParametersXmlEventArgs` | Cloud params changed |
| `OnRefreshDiscoveryConfig` | `RefreshDiscoveryConfigEventArgs` | Discovery config changed |
| `OnFeatureFlagChanged` | `FeatureFlagChangedEventArgs` | Feature flag toggled |

### 7.2 Event Subscribers

**`BufferedECEClient`** subscribes (line 95–98):
```csharp
this.cacheInvalidator.OnRefreshActionPlanInstance += this.RefreshActionPlanInstance;
this.cacheInvalidator.OnRefreshUpdateActionPlanInstances += this.RefreshUpdateActionPlanInstances;
this.cacheInvalidator.OnMonitorUpdateInstallationActionPlanInstance += this.MonitorUpdateActionPlanInstance;
this.cacheInvalidator.OnRefreshCloudParametersXml += this.RefreshCloudParameters;
```

### 7.3 Delivery Guarantees

Standard .NET event pattern — at-most-once, no deduplication. If a subscriber throws, subsequent subscribers may not be called. No message queuing or persistence.

---