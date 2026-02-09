# Orchestration Plan

**Generated:** 2026-02-09 03:19:48 UTC
**Model:** claude-opus-4.6
**Total Agents:** 11
**Waves:** 1

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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 1. Registration & Wiring

Trace the complete DI registration chain for this feature from application entry point to leaf services:

- **Service registrations** — every `AddScoped`, `AddSingleton`, `AddTransient`, `TryAdd*`, custom `IServiceCollection` extension method. Show the exact file, line, and what is being registered.
- **Factory registrations** — any factory classes, `Func<T>` delegates, or factory methods registered in DI. Trace what each factory produces and how it decides which concrete type to create (lookup tables, dictionaries, switch statements, naming conventions, etc.).
- **Maps & lookup tables populated at startup** — any `Dictionary<,>`, `ConcurrentDictionary<,>`, or mapping collections that are built during registration/startup and consumed at runtime. Show exactly what populates them and what reads from them.
- **Configuration binding** — every `IOptions<T>`, `IOptionsSnapshot<T>`, `Configure<T>`, `Bind()`, and the config sections/keys they read from. Trace from appsettings.json / env vars through to the consuming code.
- **Decorator / middleware chains** — any wrapping, decoration, or pipeline assembly related to this feature.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 2. Serialization & Type Handling

Many features have custom serialization that is easy to miss. Trace thoroughly:

- **Custom serializers** — any classes implementing `JsonConverter<T>`, `IBufferWriter`, `ISerializer`, `IHybridCacheSerializer`, `MessagePack` formatters, or similar. Show the exact type conversion logic.
- **Serializer factories** — if a factory or resolver selects which serializer to use at runtime, trace how that selection works. What map or registry does it consult? How are entries added?
- **Model-level annotations** — `[JsonConverter(typeof(...))]`, `[MessagePackObject]`, `[DataContract]`, or any attribute on model classes that controls serialization. List every model class that participates.
- **DI integration for serializers** — how are custom converters/serializers injected into the DI container? Are they added individually, scanned by convention, or registered via a module?


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 3. Storage & Persistence Layers

Trace every layer where data related to this feature is stored or cached:

- **Caching** — identify every caching layer this feature touches (in-process, distributed, file-backed, multi-tier, or custom). For each layer: how are entries keyed? What is the write path from source data to stored form? What is the read path back? What serialization or transformation happens at each boundary? What are the eviction/expiration policies? If multiple layers exist, trace how they coordinate (cache-aside, read-through, write-through, promotion/demotion between tiers).
- **Database / structured persistence** — any ORM mappings, query builders, raw queries, repositories, or data access objects. Show entity-to-table mappings, query methods, and migration history.
- **File / blob / unstructured persistence** — any direct file I/O, blob storage, or unstructured data writes. Show paths, formats, and the code performing reads and writes.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 4. Runtime Behavior & Data Flow

Trace the feature's runtime execution paths end-to-end:

- **Entry points** — every place where this feature is triggered: API controllers, CLI commands, background jobs, event handlers, scheduled tasks, message consumers.
- **Call chains** — for each entry point, trace the full call chain through services, repositories, caches, serializers, and external calls. Show the sequence of method calls.
- **Branching logic** — any conditional behavior: feature flags, environment checks, configuration-driven branching, strategy patterns. Show what conditions lead to which code paths.
- **Error handling** — try/catch blocks, retry policies, circuit breakers, fallback behavior. What happens when each component fails?


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 5. Resilience & Failure Handling

Trace how this feature behaves when things go wrong:

- **Failover** — if a primary dependency (cache, database, external service) is unavailable, is there a fallback path? Trace the exact failover logic: what triggers it, what the fallback does, and whether the caller is aware of degraded mode.
- **Retry & backoff** — any retry policies (Polly, custom loops, exponential backoff). Show the retry count, delay strategy, and what exceptions are retried vs. thrown.
- **Circuit breakers** — any circuit breaker patterns that protect against cascading failures. Show thresholds, half-open behavior, and recovery.
- **Timeout handling** — how are slow dependencies handled? Show timeout values, cancellation token propagation, and what happens when a timeout fires.
- **Graceful degradation** — does the feature continue with partial results, stale data, or reduced functionality when a dependency fails? Trace the exact behavior.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 6. Concurrency & Thread Safety

Analyze how this feature behaves under concurrent access:

- **Shared mutable state** — identify every field, property, static, or singleton that can be accessed by multiple threads. Is access synchronized? How?
- **Synchronization primitives** — locks, semaphores, `SemaphoreSlim`, `Interlocked` operations, `ReaderWriterLockSlim`, `Monitor`. Show what they protect and whether the granularity is correct.
- **Async coordination** — `Channel<T>`, `ConcurrentQueue`, `TaskCompletionSource`, `AsyncLock`, or similar. Trace producer/consumer relationships.
- **Race conditions** — identify any code paths where a race condition could occur: check-then-act without locking, double-checked locking done incorrectly, cache stampede on expiration, concurrent initialization of lazy singletons.
- **Deadlock risk** — any nested lock acquisitions or `async` calls inside locks that could deadlock. Flag these explicitly.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 7. Messaging & Event-Driven Behavior

If this feature participates in any messaging or event-driven patterns, trace them:

- **Event publishers** — what events or messages does this feature produce? Show the event types, where they are published, and the trigger conditions.
- **Event subscribers / handlers** — what events does this feature consume? Show the handler classes, what they do on receipt, and how they are registered.
- **Message contracts** — the shape of messages (classes, records, schemas). Are they versioned? How are breaking changes handled?
- **Delivery guarantees** — at-least-once, at-most-once, exactly-once? Is there deduplication or idempotency logic?
- **If none exist** — explicitly state that no messaging or pub/sub patterns were found for this feature.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 8. Observability

- **Logging** — every log statement related to this feature. Show the log level, message template, structured properties, and location. Are failures logged with enough context to diagnose issues in production?
- **Metrics** — counters, gauges, histograms, timers. What is measured? Show metric names, labels/tags, and where they are recorded.
- **Distributed tracing** — `Activity`, `ActivitySource`, OpenTelemetry spans. Show span names, attributes, and parent-child relationships.
- **Health checks** — any health check implementations that report on this feature's status.
- **Gaps** — identify failure paths, slow paths, or key decision points that are NOT observable (no logging, no metrics, no tracing).


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 9. Performance

Analyze the performance characteristics of this feature:

- **Hot paths** — identify the most frequently executed code paths. Are there unnecessary allocations, redundant computations, or N+1 query patterns?
- **Memory** — large object allocations, object pooling, buffer management, disposable resources. Are there potential memory leaks (event handlers not unsubscribed, caches growing unbounded)?
- **I/O efficiency** — batching vs. individual calls, streaming vs. buffering, connection pooling. Are external calls (DB, HTTP, file) made efficiently?
- **Scalability bottlenecks** — single-threaded chokepoints, global locks, shared state that prevents horizontal scaling. Would this feature work correctly with multiple instances?
- **Lazy vs. eager initialization** — are expensive resources initialized on first use or at startup? What is the cold-start impact?


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 10. Cross-Cutting Concerns

- **Validation** — input validation, guard clauses, invariant enforcement.
- **Authorization** — permission checks, policy enforcement on endpoints or services.


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
You are an expert code archaeologist. Given the feature **hybridcache**, trace every line of code in this repository that is linked to this feature. Do not summarize — be exhaustive. Your goal is to produce a complete map that a developer could use to fully understand and safely modify this feature.

**CRITICAL INSTRUCTION**: For every class, interface, or method you identify, follow ALL references to it. If a factory creates an object, trace what that object does. If a map or dictionary is populated at startup, show what reads from it at runtime. If a custom converter or serializer exists, show exactly where and how it is registered, resolved, and invoked. Do not stop at the surface — trace the full chain until you reach the leaf behavior.

## 11. Testing

- **Unit tests** — list every test class that directly tests a component of this feature. Show what is mocked and what is asserted.
- **Integration tests** — tests that exercise the feature end-to-end. What test infrastructure do they use (TestServer, containers, in-memory providers)?
- **Test gaps** — components or code paths identified above that have NO test coverage.


```

## Execution Results

**Completed:** 2026-02-09 03:26:53 UTC
**Total Wall Clock:** 461.2s
**Succeeded:** 11 / 11

| Status | Agent | Model | Duration | In Tokens | Out Tokens |
|--------|-------|-------|----------|-----------|------------|
| ✅ | 1-registration-wiring | claude-opus-4.6 | 250.3s | 1,260,669 | 13,966 |
| ✅ | 2-serialization-type-handling | claude-opus-4.6 | 223.6s | 1,003,567 | 12,557 |
| ✅ | 3-storage-persistence-layers | claude-opus-4.6 | 279.1s | 1,755,844 | 14,458 |
| ✅ | 4-runtime-behavior-data-flow | claude-opus-4.6 | 275.3s | 865,168 | 18,396 |
| ✅ | 5-resilience-failure-handling | claude-opus-4.6 | 181.4s | 596,149 | 10,450 |
| ✅ | 6-concurrency-thread-safety | claude-opus-4.6 | 180.1s | 495,750 | 9,724 |
| ✅ | 7-messaging-event-driven-behavior | claude-opus-4.6 | 122.0s | 362,034 | 6,395 |
| ✅ | 8-observability | claude-opus-4.6 | 194.3s | 626,125 | 11,416 |
| ✅ | 9-performance | claude-opus-4.6 | 184.6s | 621,350 | 9,720 |
| ✅ | 10-cross-cutting-concerns | claude-opus-4.6 | 216.6s | 1,176,298 | 10,475 |
| ✅ | 11-testing | claude-opus-4.6 | 201.3s | 1,044,143 | 11,078 |

**Total Tokens:** 9,807,097 in → 128,635 out


