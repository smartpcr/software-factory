## 2. Serialization & Type Handling

### 2.1 Serializer Interface Hierarchy

```
IHybridCacheSerializer<T>              (Microsoft.Extensions.Caching.Hybrid — .NET library)
  └── ICachedItemSerializer<T>         (src\Common\Common\Source\Serialization\ICachedItemSerializer.cs)
        ├── byte[] Serialize(T)
        ├── T Deserialize(byte[])
        ├── ValueTask<byte[]> SerializeAsync(T, CancellationToken)
        └── ValueTask<T> DeserializeAsync(byte[], CancellationToken)
```

`ICachedItemSerializer<T>` extends `IHybridCacheSerializer<T>` (which defines `Serialize(T, IBufferWriter<byte>)` and `Deserialize(ReadOnlySequence<byte>)`).

### 2.2 `SerializerFactory` — Runtime Selection Logic

**File**: `SerializerFactory.cs`, method `TryCreateSerializer<T>()` (line ~64)

Selection priority (first match wins):

| Priority | Condition | Serializer | Library |
|----------|-----------|-----------|---------|
| 1 | Type matches `XElement` or name = "CloudParametersXml" | `DefaultXElementSerializer<T>` | `XmlSerializer` / `XElement.Save()` |
| 2 | Type namespace contains "ECE" | `DefaultTextSerializer<T>` | `System.Text.Json` |
| 3 | Custom `JsonSerializerSettings` registered for type | `HybridJsonSerializer<T>` | `Newtonsoft.Json` |
| 4 | Type name = "InstalledVersion" | `DefaultTextSerializer<T>` | `System.Text.Json` |
| 5 | Type name = "EnvironmentReadinessTestResult" | `DefaultTextSerializer<T>` | `System.Text.Json` |
| 6 | Type namespace in `updateNamespaces` | `DefaultBinarySerializer<T>` | `BinaryFormatter` |
| 7 | Fallback | `DefaultTextSerializer<T>` | `System.Text.Json` |

**In production** for the 4 main domain types (`Update`, `UpdateRun`, `UpdateLocation`, `DiscoveryDiagnosticInfo`), priority 3 is hit because `UnityConfig` calls `RegisterConverter<T>()` for each, so they serialize with `HybridJsonSerializer<T>` (Newtonsoft.Json) using their custom `JsonConverter<T>`.

### 2.3 All Serializer Implementations

| Serializer Class | File | Underlying Library | Used For |
|-----------------|------|--------------------|----------|
| `DefaultBinarySerializer<T>` | `DefaultBinarySerializer.cs` | `BinaryFormatter` + `KnownTypesBinder` | Types in `Microsoft.AzureStack.Services.Update.*` namespace (prod fallback) |
| `DefaultJsonSerializer<T>` | `DefaultJsonSerializer.cs` | `Newtonsoft.Json` | Available but not selected by factory in production |
| `DefaultTextSerializer<T>` | `DefaultTextSerializer.cs` | `System.Text.Json` | ECE models, InstalledVersion, EnvironmentReadinessTestResult |
| `HybridJsonSerializer<T>` | `HybridJsonSerializer.cs` | `Newtonsoft.Json` + custom `JsonSerializerSettings` | **Update, UpdateRun, UpdateLocation, DiscoveryDiagnosticInfo** (via registered converters) |
| `HybridJsonTextSerializer<T>` | `HybridJsonTextSerializer.cs` | `System.Text.Json` | Available via `CreateSerializer(SerializerType.TextJson)` |
| `JsonTextSerializer<T>` | `JsonTextSerializer.cs` | `System.Text.Json` | Standalone usage |
| `DefaultDataContractSerializer<T>` | `DefaultDataContractSerializer.cs` | `DataContractSerializer` | Available via `CreateSerializer(SerializerType.DataContract)` |
| `DefaultXElementSerializer<T>` | `DefaultXElementSerializer.cs` | `XElement.Save/Load` + `XmlSerializer` | `CloudParametersXml` (XElement type) |
| `MsgPackSerializer<T>` | `MsgPackSerializer.cs` | `MessagePack` with `ContractlessStandardResolver` | Available via `CreateSerializer(SerializerType.MessagePack)` |

### 2.4 Custom JSON Converters

Registered in `UnityConfig.cs` lines 537–540:

| Converter | File | Registered For |
|-----------|------|---------------|
| `UpdateJsonConverter` | `Models\Core\Serialization\UpdateJsonConverter.cs` | `Update` |
| `UpdateRunJsonConverter` | `Models\Core\Serialization\UpdateRunJsonConverter.cs` | `UpdateRun` |
| `UpdateLocationJsonConverter` | `Models\Core\Serialization\UpdateLocationJsonConverter.cs` | `UpdateLocation` |
| `DiscoveryDiagnosticInfoJsonConverter` | `Models\Core\Serialization\DiscoveryDiagnosticInfoJsonConverter.cs` | `DiscoveryDiagnosticInfo` |

### 2.5 `KnownTypesBinder` (Security Boundary)

**File**: `KnownTypesBinder.cs`

Used only by `DefaultBinarySerializer<T>`. Restricts `BinaryFormatter` deserialization to:
- .NET Framework types (System.Collections, System.Linq, etc.)
- Approved namespaces: `Microsoft.AzureStack.Services.Update`, `Microsoft.AzureStack.UpdateService`, `Microsoft.EnvironmentReadiness`, `Microsoft.AzureStack.Services.Fabric`
- Generic types with all-allowed type arguments
- Built-in types

### 2.6 `ICachedItem` Interface

**File**: `ICachedItem.cs`

Every type stored in `DefaultHybridCache<T>` must implement:
```csharp
public interface ICac
    CacheKey GetCacheKey();
    DateTime GetLastModifiedTime();  // used for staleness calculation
    string GetStateLogMessage
```

---