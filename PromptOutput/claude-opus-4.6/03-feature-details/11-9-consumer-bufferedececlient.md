## 9. Consumer: `BufferedECEClient`

**BufferedECEClient.cs** — Primary production consumer of the HybridCache system.

### Cache Instances Created

| Field | Type | Created At | TTL Source |
|---|---|---|---|
| `actionPlanInstanceCache` | `IHybridCache<ActionPlanInstance>` | line 67-70 | `actionPlanInstanceCacheSettings` or `settings.ActionPlanInstanceExpirationTime` |
| `cloudParametersXML` | `AutoCacheECEResult<CloudParametersXml>` | line 79-88 | `cloudParamsCacheSettings` or `settings.CloudDefinitionExpirationTime` |

### Cache Usage Points

| Method | Operation | Lines |
|---|---|---|
| `GetActionPlanInstance` | `actionPlanInstanceCache.GetOrAddAsync` | 96-107 |
| `GetActionPlanInstances` | `actionPlanInstanceCache.GetOrAddListAsync` | 114-126 |
| `CreateActionPlanInstance` | `actionPlanInstanceCache.SetAsync` + `AddInstanceToListCache` | 133-140 |
| `GetStampInformation` et al. | `cloudParametersXML.GetValueAsync` | 146-153 |
| `SetPackageVersion` et al. | `cloudParametersXML.UpdateValueInBackground` | 157-174 |
| `RefreshActionPlanInstance` | `cacheInvalidator.InvalidateCache` | 178-214 |
| `RefreshUpdateActionPlanInstances` | `cacheInvalidator.InvalidateCache` (list) | 216-247 |
| `AddInstanceToListCache` | `GetActionPlanInstances` + `cacheInvalidator.InvalidateCache` | 249-280 |
| `MonitorUpdateActionPlanInstance` | `GetActionPlanInstance` + `cacheInvalidator.InvalidateCache` | 282-367 |
| `RefreshCloudParameters` | `cacheInvalidator.InvalidateCache` | 369-394 |

### Cache Invalidation Wiring (lines 89-92)
```csharp
cacheInvalidator.OnRefreshActionPlanInstance += RefreshActionPlanInstance
cacheInvalidator.OnRefreshUpdateActionPlanInstances += RefreshUpdateActionPlanInstances
cacheInvalidator.OnMonitorUpdateInstallationActionPlanInstance += MonitorUpdateActionPlanInstance
cacheInvalidator.OnRefreshCloudParametersXml += RefreshCloudParameters
```

---