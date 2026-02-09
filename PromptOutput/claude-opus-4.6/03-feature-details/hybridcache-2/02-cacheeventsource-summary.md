## CacheEventSource Summary

**Location:** `E:\work\hci\update\src\Common\Common\Source\Trace\CacheEventSource.cs`

### Event IDs & Names (from CacheEventIds)
| ID | Event Name |
|---|---|
| 3201 | CacheHit |
| 3202 | CacheMiss |
| 3203 | CacheGet |
| 3204 | CacheGetFailed |
| 3205 | CacheSet |
| 3206 | CacheSetFailed |
| 3207 | CacheRemove |
| 3208 | CacheRemoveFailed |
| 3209 | CacheRefresh |
| 3210 | CacheRefreshFailed |
| 3211 | CacheExpired |
| 3212 | ReportCacheFileSize |
| 3213 | CacheCorrupted |
| 3214 | CacheSettingError |
| 3215 | CacheSerializerError |
| 3216 | CacheUnknownError |
| 3217 | ReportCacheStatistics |
| 3218 | CacheSerializerWarning |
| 3219 | HandleCacheInvalidation |

### EventCounter Definitions
The class defines 6 event counters, initialized in `OnEventCommand()` when enabled:
- **CacheHit** - Counter for cache hit operations
- **CacheMiss** - Counter for cache miss operations
- **CacheGet** - Counter for cache get operations
- **CacheSet** - Counter for cache set operations
- **CacheRemove** - Counter for cache remove operations
- **CacheSize** - Counter for cache size metrics

### Key Features
- **EventSource Name:** `Microsoft-UpdateService-CacheEventSource`
- **Singleton Pattern:** Uses static Instance property
- **Event Levels:** Verbose, Informational, Warning, and Error levels
- Includes diagnostic context parameters (filePath, memberName, lineNumber, correlationId)
- Supports metric reporting via `OnEvent*()` methods and EventCounter API
Now let me read the specific HybridCache test files to extract test method names and mocked items:
Let me get the actual test method names by searching more specifically:
Let me get full content for HybridCacheTests to extract all test methods:
Perfect! Now let me get the other test files' full content:
Perfect! Now I have all the information needed. Let me compile the report: