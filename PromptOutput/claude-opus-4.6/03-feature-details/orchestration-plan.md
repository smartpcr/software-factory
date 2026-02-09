# Orchestration Plan

**Generated:** 2026-02-09 04:23:40 UTC
**Model:** claude-opus-4.6
**Total Agents:** 13
**Waves:** 3

## Original Prompt

```
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 1. Registration & Wiring

Trace the complete DI registration chain for this feature from application entry point to leaf services:

- **Service registrations** — every `AddScoped`, `AddSingleton`, `AddTransient`, `TryAdd*`, custom `IServiceCollection` extension method. Show the exact file, line, and what is being registered.
- **Factory registrations** — any factory classes, `Func<T>` delegates, or factory methods registered in DI. Trace what each factory produces and how it decides which concrete type to create (lookup tables, dictionaries, switch statements, naming conventions, etc.).
- **Maps & lookup tables populated at startup** — any `Dictionary<,>`, `ConcurrentDictionary<,>`, or mapping collections that are built during registration/startup and consumed at runtime. Show exactly what populates them and what reads from them.
- **Configuration binding** — every `IOptions<T>`, `IOptionsSnapshot<T>`, `Configure<T>`, `Bind()`, and the config sections/keys they read from. Trace from appsettings.json / env vars through to the consuming code.
- **Decorator / middleware chains** — any wrapping, decoration, or pipeline assembly related to this feature.

## 2. Serialization & Type Handling

Many features have custo
... (truncated)
```

## Execution DAG

### Wave 1 (parallel)

- **1-registration-wiring** (GeneralPurpose) — depends on: none
- **2-serialization-type-handling** (GeneralPurpose) — depends on: none
- **3-storage-persistence-layers** (GeneralPurpose) — depends on: none
- **4-runtime-behavior-data-flow** (GeneralPurpose) — depends on: none
- **5-resilience-failure-handling** (GeneralPurpose) — depends on: none
- **6-concurrency-thread-safety** (GeneralPurpose) — depends on: none
- **7-messaging-event-driven-behavior** (GeneralPurpose) — depends on: none
- **8-observability** (GeneralPurpose) — depends on: none
- **9-performance** (GeneralPurpose) — depends on: none
- **10-cross-cutting-concerns** (GeneralPurpose) — depends on: none
- **11-testing** (GeneralPurpose) — depends on: none

### Wave 2 (sequential)

- **synthesis** (GeneralPurpose) — depends on: 1-registration-wiring, 2-serialization-type-handling, 3-storage-persistence-layers, 4-runtime-behavior-data-flow, 5-resilience-failure-handling, 6-concurrency-thread-safety, 7-messaging-event-driven-behavior, 8-observability, 9-performance, 10-cross-cutting-concerns, 11-testing

### Wave 3 (sequential)

- **distill** (GeneralPurpose) — depends on: synthesis

## Subagent Details

### 1. 1-registration-wiring

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 1. Registration & Wiring |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 1. Registration & Wiring

Trace the complete DI registration chain for this feature from application entry point to leaf services:

- **Service registrations** — every `AddScoped`, `AddSingleton`, `AddTransient`, `TryAdd*`, custom `IServiceCollection` extension method. Show the exact file, line, and what is being registered.
- **Factory registrations** — any factory classes, `Func<T>` delegates, or factory methods registered in DI. Trace what each factory produces and how it decides which concrete type to create (lookup tables, dictionaries, switch statements, naming conventions, etc.).
- **Maps & lookup tables populated at startup** — any `Dictionary<,>`, `ConcurrentDictionary<,>`, or mapping collections that are built during registration/startup and consumed at runtime. Show exactly what populates them and what reads from them.
- **Configuration binding** — every `IOptions<T>`, `IOptionsSnapshot<T>`, `Configure<T>`, `Bind()`, and the config sections/keys they read from. Trace from appsettings.json / env vars through to the consuming code.
- **Decorator / middleware chains** — any wrapping, decoration, or pipeline assembly related to this feature.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization
... (truncated)
```

### 2. 2-serialization-type-handling

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 2. Serialization & Type Handling |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 2. Serialization & Type Handling

Many features have custom serialization that is easy to miss. Trace thoroughly:

- **Custom serializers** — any classes implementing `JsonConverter<T>`, `IBufferWriter`, `ISerializer`, `IHybridCacheSerializer`, `MessagePack` formatters, or similar. Show the exact type conversion logic.
- **Serializer factories** — if a factory or resolver selects which serializer to use at runtime, trace how that selection works. What map or registry does it consult? How are entries added?
- **Model-level annotations** — `[JsonConverter(typeof(...))]`, `[MessagePackObject]`, `[DataContract]`, or any attribute on model classes that controls serialization. List every model class that participates.
- **DI integration for serializers** — how are custom converters/serializers injected into the DI container? Are they added individually, scanned by convention, or registered via a module?



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    
... (truncated)
```

### 3. 3-storage-persistence-layers

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 3. Storage & Persistence Layers |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 3. Storage & Persistence Layers

Trace every layer where data related to this feature is stored or cached:

- **Caching** — identify every caching layer this feature touches (in-process, distributed, file-backed, multi-tier, or custom). For each layer: how are entries keyed? What is the write path from source data to stored form? What is the read path back? What serialization or transformation happens at each boundary? What are the eviction/expiration policies? If multiple layers exist, trace how they coordinate (cache-aside, read-through, write-through, promotion/demotion between tiers).
- **Database / structured persistence** — any ORM mappings, query builders, raw queries, repositories, or data access objects. Show entity-to-table mappings, query methods, and migration history.
- **File / blob / unstructured persistence** — any direct file I/O, blob storage, or unstructured data writes. Show paths, formats, and the code performing reads and writes.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;

... (truncated)
```

### 4. 4-runtime-behavior-data-flow

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 4. Runtime Behavior & Data Flow |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 4. Runtime Behavior & Data Flow

Trace the feature's runtime execution paths end-to-end:

- **Entry points** — every place where this feature is triggered: API controllers, CLI commands, background jobs, event handlers, scheduled tasks, message consumers.
- **Call chains** — for each entry point, trace the full call chain through services, repositories, caches, serializers, and external calls. Show the sequence of method calls.
- **Branching logic** — any conditional behavior: feature flags, environment checks, configuration-driven branching, strategy patterns. Show what conditions lead to which code paths.
- **Error handling** — try/catch blocks, retry policies, circuit breakers, fallback behavior. What happens when each component fails?



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class HybridCacheFactoryTests
    {
        private FakeTime clock;
        private IMemoryCache memoryCache;
        private IDistrib
... (truncated)
```

### 5. 5-resilience-failure-handling

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 5. Resilience & Failure Handling |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 5. Resilience & Failure Handling

Trace how this feature behaves when things go wrong:

- **Failover** — if a primary dependency (cache, database, external service) is unavailable, is there a fallback path? Trace the exact failover logic: what triggers it, what the fallback does, and whether the caller is aware of degraded mode.
- **Retry & backoff** — any retry policies (Polly, custom loops, exponential backoff). Show the retry count, delay strategy, and what exceptions are retried vs. thrown.
- **Circuit breakers** — any circuit breaker patterns that protect against cascading failures. Show thresholds, half-open behavior, and recovery.
- **Timeout handling** — how are slow dependencies handled? Show timeout values, cancellation token propagation, and what happens when a timeout fires.
- **Graceful degradation** — does the feature continue with partial results, stale data, or reduced functionality when a dependency fails? Trace the exact behavior.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
... (truncated)
```

### 6. 6-concurrency-thread-safety

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 6. Concurrency & Thread Safety |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 6. Concurrency & Thread Safety

Analyze how this feature behaves under concurrent access:

- **Shared mutable state** — identify every field, property, static, or singleton that can be accessed by multiple threads. Is access synchronized? How?
- **Synchronization primitives** — locks, semaphores, `SemaphoreSlim`, `Interlocked` operations, `ReaderWriterLockSlim`, `Monitor`. Show what they protect and whether the granularity is correct.
- **Async coordination** — `Channel<T>`, `ConcurrentQueue`, `TaskCompletionSource`, `AsyncLock`, or similar. Trace producer/consumer relationships.
- **Race conditions** — identify any code paths where a race condition could occur: check-then-act without locking, double-checked locking done incorrectly, cache stampede on expiration, concurrent initialization of lazy singletons.
- **Deadlock risk** — any nested lock acquisitions or `async` calls inside locks that could deadlock. Flag these explicitly.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualS
... (truncated)
```

### 7. 7-messaging-event-driven-behavior

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 7. Messaging & Event-Driven Behavior |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 7. Messaging & Event-Driven Behavior

If this feature participates in any messaging or event-driven patterns, trace them:

- **Event publishers** — what events or messages does this feature produce? Show the event types, where they are published, and the trigger conditions.
- **Event subscribers / handlers** — what events does this feature consume? Show the handler classes, what they do on receipt, and how they are registered.
- **Message contracts** — the shape of messages (classes, records, schemas). Are they versioned? How are breaking changes handled?
- **Delivery guarantees** — at-least-once, at-most-once, exactly-once? Is there deduplication or idempotency logic?
- **If none exist** — explicitly state that no messaging or pub/sub patterns were found for this feature.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class HybridCacheFactoryTests
    {
        private FakeTime clock;
        private IMemoryCache m
... (truncated)
```

### 8. 8-observability

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 8. Observability |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 8. Observability

- **Logging** — every log statement related to this feature. Show the log level, message template, structured properties, and location. Are failures logged with enough context to diagnose issues in production?
- **Metrics** — counters, gauges, histograms, timers. What is measured? Show metric names, labels/tags, and where they are recorded.
- **Distributed tracing** — `Activity`, `ActivitySource`, OpenTelemetry spans. Show span names, attributes, and parent-child relationships.
- **Health checks** — any health check implementations that report on this feature's status.
- **Gaps** — identify failure paths, slow paths, or key decision points that are NOT observable (no logging, no metrics, no tracing).



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class HybridCacheFactoryTests
    {
        private FakeTime clock;
        private IMemoryCache memoryCache;
        private IDistributedCache fileCache;
 
... (truncated)
```

### 9. 9-performance

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 9. Performance |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 9. Performance

Analyze the performance characteristics of this feature:

- **Hot paths** — identify the most frequently executed code paths. Are there unnecessary allocations, redundant computations, or N+1 query patterns?
- **Memory** — large object allocations, object pooling, buffer management, disposable resources. Are there potential memory leaks (event handlers not unsubscribed, caches growing unbounded)?
- **I/O efficiency** — batching vs. individual calls, streaming vs. buffering, connection pooling. Are external calls (DB, HTTP, file) made efficiently?
- **Scalability bottlenecks** — single-threaded chokepoints, global locks, shared state that prevents horizontal scaling. Would this feature work correctly with multiple instances?
- **Lazy vs. eager initialization** — are expensive resources initialized on first use or at startup? What is the cold-start impact?



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public cl
... (truncated)
```

### 10. 10-cross-cutting-concerns

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 10. Cross-Cutting Concerns |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 10. Cross-Cutting Concerns

- **Validation** — input validation, guard clauses, invariant enforcement.
- **Authorization** — permission checks, policy enforcement on endpoints or services.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class HybridCacheFactoryTests
    {
        private FakeTime clock;
        private IMemoryCache memoryCache;
        private IDistributedCache fileCache;
        private HybridCache innerCache;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void Initialize()
        {
            var services = new ServiceCollection();
            this.clock = new FakeTime();
            var globalSettings = CacheSettings.GetCacheSettings(new Dictionary<string, string>());
            this.memoryCache = new MemoryCache(
                new MemoryCacheOptions
                {
                    Clock = clock,
                    SizeLimit = globa
... (truncated)
```

### 11. 11-testing

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | none |
| **Description** | 11. Testing |
| **Status** | ⏳ Pending |

**Prompt:**

```
IMPORTANT: All relevant source files are included below as code blocks.
Analyze ONLY the provided code to answer the question.
Do NOT use tools to read, search, or explore files.
When referencing code, use filename:lineNumber range (e.g., MyService.cs:42-58) instead of pasting code snippets.

You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 11. Testing

- **Unit tests** — list every test class that directly tests a component of this feature. Show what is mocked and what is asserted.
- **Integration tests** — tests that exercise the feature end-to-end. What test infrastructure do they use (TestServer, containers, in-memory providers)?
- **Test gaps** — components or code paths identified above that have NO test coverage.



---
SOURCE FILES (76 files):

### src\Common\Common\UnitTest\HybridCacheFactoryTests.cs
```csharp
//-------------------------------------------------------------------------------
// <copyright file="HybridCacheFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace Microsoft.AzureStack.Services.Update.Common.UnitTest
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System;
    using System.Threading.Tasks;
    using Caching;
    using FluentAssertions;
    using Microsoft.AzureStack.Services.Update.Common.Serialization;
    using Microsoft.Extensions.Caching.Distributed;
    using Microsoft.Extensions.Caching.Hybrid;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.DependencyInjection;
    using VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class HybridCacheFactoryTests
    {
        private FakeTime clock;
        private IMemoryCache memoryCache;
        private IDistributedCache fileCache;
        private HybridCache innerCache;

        public TestContext TestContext { get; set; }

        [TestInitialize]
        public void Initialize()
        {
            var services = new ServiceCollection();
            this.clock = new FakeTime();
            var globalSettings = CacheSettings.GetCacheSettings(new Dictionary<st
... (truncated)
```

### 12. synthesis

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | 1-registration-wiring, 2-serialization-type-handling, 3-storage-persistence-layers, 4-runtime-behavior-data-flow, 5-resilience-failure-handling, 6-concurrency-thread-safety, 7-messaging-event-driven-behavior, 8-observability, 9-performance, 10-cross-cutting-concerns, 11-testing |
| **Description** | Combine all section analyses into comprehensive document with TOC |
| **Status** | ⏳ Pending |

**Prompt:**

```
You are given the results of detailed code analysis sections. Combine them into a single comprehensive document.

Requirements:
1. Start with a Table of Contents listing all sections
2. Include ALL content from each section, preserving ## headers
3. Ensure cross-references between sections are consistent
4. Do not add new analysis — only organize and present what was provided

Section results:

--- 1-registration-wiring ---
{result:1-registration-wiring}

--- 2-serialization-type-handling ---
{result:2-serialization-type-handling}

--- 3-storage-persistence-layers ---
{result:3-storage-persistence-layers}

--- 4-runtime-behavior-data-flow ---
{result:4-runtime-behavior-data-flow}

--- 5-resilience-failure-handling ---
{result:5-resilience-failure-handling}

--- 6-concurrency-thread-safety ---
{result:6-concurrency-thread-safety}

--- 7-messaging-event-driven-behavior ---
{result:7-messaging-event-driven-behavior}

--- 8-observability ---
{result:8-observability}

--- 9-performance ---
{result:9-performance}

--- 10-cross-cutting-concerns ---
{result:10-cross-cutting-concerns}

--- 11-testing ---
{result:11-testing}

```

### 13. distill

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | synthesis |
| **Description** | Produce knowledge graph (Mermaid) and context document |
| **Status** | ⏳ Pending |

**Prompt:**

```
You are given a comprehensive feature analysis document. Produce exactly two sections
with the headers shown below. Output ONLY these two sections, no preamble.

## Knowledge Graph

Generate a Mermaid diagram capturing every component discovered in the analysis:
- Nodes: classes, interfaces, factories, maps/registries, serializers, converters,
  config classes, endpoints, test classes, external dependencies
- Edges: implements, creates, resolves-from, populates, reads-from, serializes-with,
  persists-to, calls, tests, configures, publishes, subscribes, retries, falls-back-to,
  locks, observes
- Subgraphs: group nodes by layer (Registration, Serialization, Storage, Runtime,
  Resilience, Concurrency, Messaging, Observability, Testing)

Follow the diagram with a prose summary of the end-to-end feature lifecycle.

## Context Document

Produce a concise, self-contained reference document (under 4000 words) that a developer
or AI agent could use as system-prompt context to understand and safely modify this feature.
Include:
- Architecture overview and end-to-end lifecycle
- Key components and relationships (DI registrations, factories, configuration)
- Non-obvious wiring (factory maps, custom serializer resolution, multi-layer caching)
- Critical invariants and constraints that must be preserved
- Where to add new functionality and what registrations must be updated
- Concurrency and thread-safety requirements
- Error handling and resilience patterns
- Test coverage and known gaps

Use specific class names, method names, and file paths. Write it as a reference document,
not a narrative summary.

---
ANALYSIS DOCUMENT:
{result:synthesis}
```

## Execution Results

**Completed:** 2026-02-09 04:34:35 UTC
**Total Wall Clock:** 692.6s
**Succeeded:** 13 / 13

| Status | Agent | Model | Duration | In Tokens | Out Tokens |
|--------|-------|-------|----------|-----------|------------|
| ✅ | 1-registration-wiring | claude-opus-4.6 | 258.7s | 53,017 | 15,368 |
| ✅ | 2-serialization-type-handling | claude-opus-4.6 | 87.1s | 52,954 | 4,569 |
| ✅ | 3-storage-persistence-layers | claude-opus-4.6 | 177.5s | 52,966 | 10,228 |
| ✅ | 4-runtime-behavior-data-flow | claude-opus-4.6 | 170.7s | 52,897 | 9,584 |
| ✅ | 5-resilience-failure-handling | claude-opus-4.6 | 128.1s | 52,957 | 6,180 |
| ✅ | 6-concurrency-thread-safety | claude-opus-4.6 | 111.0s | 52,965 | 5,511 |
| ✅ | 7-messaging-event-driven-behavior | claude-opus-4.6 | 93.2s | 52,921 | 5,246 |
| ✅ | 8-observability | claude-opus-4.6 | 100.5s | 52,925 | 6,114 |
| ✅ | 9-performance | claude-opus-4.6 | 90.7s | 52,933 | 4,400 |
| ✅ | 10-cross-cutting-concerns | claude-opus-4.6 | 85.6s | 52,792 | 4,300 |
| ✅ | 11-testing | claude-opus-4.6 | 98.4s | 52,826 | 5,340 |
| ✅ | synthesis | claude-opus-4.6 | 276.9s | 70,676 | 16,000 |
| ✅ | distill | claude-opus-4.6 | 119.6s | 30,910 | 6,186 |

**Total Tokens:** 683,739 in → 99,026 out


