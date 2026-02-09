# Orchestration Plan

**Generated:** 2026-02-09 02:27:00 UTC
**Model:** claude-opus-4.6
**Total Agents:** 12
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

- **explore-di-registration** (Explore) — depends on: none
- **explore-serializers** (Explore) — depends on: none
- **explore-storage-caching** (Explore) — depends on: none
- **explore-entry-points** (Explore) — depends on: none
- **explore-resilience** (Explore) — depends on: none
- **explore-concurrency** (Explore) — depends on: none
- **explore-messaging** (Explore) — depends on: none
- **explore-observability** (Explore) — depends on: none
- **explore-tests** (Explore) — depends on: none
- **explore-cross-cutting** (Explore) — depends on: none

### Wave 2 (sequential)

- **explore-call-chains** (GeneralPurpose) — depends on: explore-entry-points

### Wave 3 (sequential)

- **explore-performance** (GeneralPurpose) — depends on: explore-call-chains

## Subagent Details

### 1. explore-di-registration

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Trace all DI registration for hybridcache: AddScoped/Singleton/Transient, factories, maps, configuration binding, decorator chains |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every Dependency Injection registration related to the 'hybridcache' feature in this .NET repository. Search for: 1) All `AddScoped`, `AddSingleton`, `AddTransient`, `TryAdd*` calls that mention 'hybrid' or 'cache'. 2) Any custom `IServiceCollection` extension methods. 3) Factory classes, `Func<T>` delegates, or factory methods that produce hybridcache types. 4) For each factory, show the decision logic (lookup tables, dictionaries, switch statements, naming conventions) that determines which concrete type is created. 5) All `Dictionary<,>`, `ConcurrentDictionary<,>`, or mapping collections populated at startup that relate to hybridcache. Show what populates them and what reads from them at runtime. 6) All `IOptions<T>`, `IOptionsSnapshot<T>`, `Configure<T>`, `Bind()` calls related to hybridcache. Trace from appsettings.json/env vars through to consuming code. 7) Any decorator or middleware chains. Show file paths, line numbers, and exact code for every registration found. Do not summarize — be exhaustive.
```

### 2. explore-serializers

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find all custom serializers, converters, and type handling for hybridcache: JsonConverter, IHybridCacheSerializer, MessagePack formatters, model annotations |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every serialization component related to hybridcache. Search for: 1) All classes implementing `JsonConverter<T>`, `IHybridCacheSerializer`, `ISerializer`, `IBufferWriter`, or MessagePack formatters that relate to hybridcache types. Show the exact type conversion logic in each. 2) Serializer factories or resolvers that select which serializer to use at runtime. Trace how selection works: what map or registry is consulted? How are entries added? 3) Model-level annotations like `[JsonConverter(typeof(...))]`, `[MessagePackObject]`, `[DataContract]` on every class that participates in hybridcache serialization. 4) How are custom converters/serializers injected into DI? Are they added individually, scanned by convention, or via modules? 5) Any wrapping or decoration of serializers. Show file paths, line numbers, and exact code for every serializer found.
```

### 3. explore-storage-caching

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Identify all storage and caching layers for hybridcache: in-process, distributed, file-backed, multi-tier caches, keying strategies, read/write paths, eviction policies |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every storage and caching layer touched by hybridcache. Search for: 1) All caching layers (in-process, distributed, file-backed, multi-tier, custom implementations). For each: how are entries keyed? What is the write path from source to stored form? What is the read path back? What serialization/transformation happens at boundaries? What are eviction/expiration policies? 2) How do multiple layers coordinate if they exist (cache-aside, read-through, write-through, promotion/demotion)? 3) Any ORM mappings, query builders, repositories, or data access objects related to hybridcache. Show entity-to-table mappings and query methods. 4) Direct file I/O, blob storage, or unstructured data writes. Show paths, formats, read/write code. 5) Any initialization or warmup of caches at startup. Show file paths, line numbers, and trace the exact flow for every storage/caching operation.
```

### 4. explore-entry-points

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find all entry points where hybridcache is triggered: API controllers, CLI commands, background jobs, event handlers, scheduled tasks |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find every place where the hybridcache feature is triggered or accessed. Search for: 1) API controllers with endpoints that use hybridcache. 2) CLI commands that invoke hybridcache. 3) Background jobs or scheduled tasks. 4) Event handlers that process events related to hybridcache. 5) Message consumers. 6) Initialization code that bootstraps hybridcache. For each entry point found, show: the full class name, method name, file path, line number, and what it does. List every entry point without omission.
```

### 5. explore-resilience

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find failover, retry policies, circuit breakers, timeout handling, and graceful degradation for hybridcache |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every resilience pattern in the hybridcache feature. Search for: 1) Failover logic — if a primary dependency (cache, database, external service) fails, is there a fallback? Trace the exact trigger, fallback behavior, and whether the caller knows about degraded mode. 2) Retry policies — Polly, custom loops, exponential backoff. Show retry count, delay strategy, which exceptions are retried vs. thrown. 3) Circuit breakers — thresholds, half-open behavior, recovery. 4) Timeout handling — show timeout values, cancellation token propagation, what happens when a timeout fires. 5) Graceful degradation — does hybridcache continue with partial results, stale data, or reduced functionality when a dependency fails? Trace the exact behavior. Show file paths, line numbers, and exact code for every resilience pattern found.
```

### 6. explore-concurrency

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Analyze concurrency and thread safety for hybridcache: shared mutable state, locks, async coordination, race conditions, deadlock risks |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively analyze concurrency and thread safety in hybridcache. Search for: 1) Every field, property, static, or singleton that can be accessed by multiple threads. Is access synchronized? How? 2) All synchronization primitives used (locks, semaphores, `SemaphoreSlim`, `Interlocked`, `ReaderWriterLockSlim`, `Monitor`). Show what they protect and whether granularity is correct. 3) Async coordination patterns (`Channel<T>`, `ConcurrentQueue`, `TaskCompletionSource`, `AsyncLock`). Trace producer/consumer relationships. 4) Identify race conditions — check-then-act without locking, double-checked locking done incorrectly, cache stampede on expiration, concurrent initialization of lazy singletons. 5) Deadlock risks — nested lock acquisitions or async calls inside locks. Flag these explicitly. Show file paths, line numbers, and code for every concurrency pattern found.
```

### 7. explore-messaging

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find messaging and event-driven patterns for hybridcache: event publishers, subscribers, message contracts, delivery guarantees |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every messaging or event-driven pattern related to hybridcache. Search for: 1) Event types or message classes that hybridcache produces. Where are they published? What triggers publication? 2) Event or message subscriber/handler classes. What events do they consume? What do they do on receipt? How are they registered? 3) Message contract definitions — classes, records, schemas. Are they versioned? How are breaking changes handled? 4) Delivery guarantees — at-least-once, at-most-once, exactly-once? Is there deduplication or idempotency logic? 5) If no messaging/pub-sub patterns exist, explicitly state that. Show file paths, line numbers, and exact code for every messaging pattern found.
```

### 8. explore-observability

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find logging, metrics, distributed tracing, and health checks for hybridcache |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and trace every observability component in hybridcache. Search for: 1) Every log statement related to hybridcache — show log level, message template, structured properties, and location. Are failures logged with enough context? 2) Metrics — counters, gauges, histograms, timers. What is measured? Show metric names, labels/tags, and where they are recorded. 3) Distributed tracing — `Activity`, `ActivitySource`, OpenTelemetry spans. Show span names, attributes, parent-child relationships. 4) Health check implementations that report on hybridcache status. 5) Identify gaps — failure paths, slow paths, or key decision points that are NOT observable (no logging, metrics, or tracing). Show file paths, line numbers, and exact code for every observability element found.
```

### 9. explore-tests

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find all unit tests, integration tests, and test gaps for hybridcache components |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find and catalog every test related to hybridcache. Search for: 1) Unit test classes that directly test hybridcache components. For each, show what is mocked and what is asserted. 2) Integration tests that exercise hybridcache end-to-end. What test infrastructure do they use (TestServer, containers, in-memory providers)? 3) Identify components or code paths that have NO test coverage. Show file paths, line numbers, class names, test method names, and test assertions for every test found.
```

### 10. explore-call-chains

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | explore-entry-points |
| **Description** | Trace complete call chains from entry points through all layers for hybridcache |
| **Status** | ⏳ Pending |

**Prompt:**

```
Using the entry points discovered separately, trace the COMPLETE call chain for each entry point from its method signature through all intermediate services, repositories, caches, serializers, and external calls. For each entry point: 1) Show the full sequence of method calls in order. 2) Show what parameters are passed down the chain and how they transform. 3) Show all branching logic (if statements, strategy patterns, feature flags) and what conditions lead to which code paths. 4) For each method in the chain, show: file path, line number, method signature, and what it does. 5) Show all external calls (database queries, HTTP calls, cache gets/sets, file I/O) with exact API calls. Be exhaustive — do not omit any method in any call chain.
```

### 11. explore-performance

| Property | Value |
|----------|-------|
| **Type** | GeneralPurpose |
| **Model** | claude-opus-4.6 |
| **Depends On** | explore-call-chains |
| **Description** | Analyze performance characteristics of hybridcache: hot paths, memory allocation, I/O efficiency, scalability bottlenecks |
| **Status** | ⏳ Pending |

**Prompt:**

```
Analyze the performance characteristics of hybridcache using all discovered information. Search for: 1) Hot paths — most frequently executed code paths. Are there unnecessary allocations, redundant computations, N+1 query patterns? 2) Memory — large object allocations, object pooling, buffer management, disposable resources. Are there potential memory leaks (event handlers not unsubscribed, unbounded cache growth)? 3) I/O efficiency — batching vs. individual calls, streaming vs. buffering, connection pooling. Are external calls (DB, HTTP, file) made efficiently? 4) Scalability bottlenecks — single-threaded chokepoints, global locks, shared state that prevents horizontal scaling. Would this feature work correctly with multiple instances? 5) Lazy vs. eager initialization — are expensive resources initialized on first use or at startup? What is the cold-start impact? Show file paths, line numbers, and code for every performance characteristic found.
```

### 12. explore-cross-cutting

| Property | Value |
|----------|-------|
| **Type** | Explore |
| **Model** | claude-haiku-4.5 |
| **Depends On** | none |
| **Description** | Find validation, authorization, and other cross-cutting concerns for hybridcache |
| **Status** | ⏳ Pending |

**Prompt:**

```
Exhaustively find all validation and authorization logic related to hybridcache. Search for: 1) Input validation — guard clauses, invariant enforcement. Show what is validated and where. 2) Authorization — permission checks, policy enforcement on endpoints or services. What roles or permissions are required to use hybridcache? 3) Any other cross-cutting concerns (logging patterns, error handling conventions, transaction management). Show file paths, line numbers, and exact code for every cross-cutting concern found.
```

## Execution Results

**Completed:** 2026-02-09 02:43:31 UTC
**Total Wall Clock:** 1030.7s
**Succeeded:** 12 / 12

| Status | Agent | Model | Duration | In Tokens | Out Tokens |
|--------|-------|-------|----------|-----------|------------|
| ✅ | explore-di-registration | claude-opus-4.6 | 194.9s | 514,103 | 11,502 |
| ✅ | explore-serializers | claude-opus-4.6 | 148.9s | 398,046 | 8,302 |
| ✅ | explore-storage-caching | claude-opus-4.6 | 175.0s | 618,447 | 8,003 |
| ✅ | explore-entry-points | claude-opus-4.6 | 169.4s | 363,752 | 10,192 |
| ✅ | explore-resilience | claude-opus-4.6 | 174.2s | 516,132 | 8,983 |
| ✅ | explore-concurrency | claude-opus-4.6 | 125.9s | 402,175 | 5,575 |
| ✅ | explore-messaging | claude-opus-4.6 | 118.0s | 277,020 | 5,194 |
| ✅ | explore-observability | claude-opus-4.6 | 134.5s | 551,119 | 7,030 |
| ✅ | explore-tests | claude-opus-4.6 | 189.6s | 667,609 | 11,271 |
| ✅ | explore-cross-cutting | claude-opus-4.6 | 138.1s | 406,081 | 7,709 |
| ✅ | explore-call-chains | claude-opus-4.6 | 442.8s | 2,003,462 | 52,481 |
| ✅ | explore-performance | claude-opus-4.6 | 232.3s | 649,957 | 17,431 |

**Total Tokens:** 7,367,903 in → 153,673 out


