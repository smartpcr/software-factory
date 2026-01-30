# Code Intelligence Requirements

> Last Updated: 2026-01-29

This document defines the requirements for repository-level code understanding through relationship extraction.

**Related Document:** [Implementation Plan](./code_intelligence_implementation_plan.md)

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Problem Statement](#problem-statement)
3. [Lessons from OpenCode](#lessons-from-opencode)
4. [Repository Understanding Framework](#repository-understanding-framework)
   - [Core Philosophy](#core-philosophy)
   - [Relationship Taxonomy](#relationship-taxonomy)
5. [Success Criteria](#success-criteria)
6. [References](#references)

---

## Executive Summary

### Current Gap

LSP-based code intelligence fails for certain architectural patterns:
- **Service Fabric hosting**: External host with reflection-based assembly loading
- **Dynamic routing**: Custom route providers, ARM-style URL generation
- **Plugin architectures**: MEF, runtime assembly loading
- **Configuration-driven behavior**: Settings.xml, manifest files

### Proposed Solution

Implement a **multi-strategy code intelligence system** that combines:
1. LSP (when available and accurate)
2. Pattern-based heuristics
3. Configuration file analysis
4. Hypergraph relationship traversal
5. Embedding-based semantic discovery

### Goals

| Goal | Description |
|------|-------------|
| **Comprehensive Understanding** | Extract all meaningful relationships from a codebase |
| **Framework Agnostic** | Support any hosting/framework pattern via declarative rules |
| **Queryable Graph** | Enable natural language and structured queries over the code graph |
| **Incremental Updates** | Efficiently update graph on file changes |

---

## Problem Statement

### Why LSP Fails for Service Fabric

**Traditional Entry Point (LSP works):**
```
Main() → Service.Start() → Controller
 └── Compile-time call chain → LSP can trace
```

**Service Fabric Pattern (LSP fails):**
```
External Host (ServiceFabricHost.exe)
        │
        ▼
Settings.xml → "ServiceAssembly=MyService.dll"
        │
        ▼ (Reflection at runtime)
UpdateService<T> → Initialize() → StartAsync()
        │
        ▼
IWebApiService<T>.Register() → Controllers
```

**Root Cause**: No compile-time linkage between host and service assembly. The connection exists only in configuration files and through reflection-based interfaces.

### Impact on Code Understanding

| Capability | LSP Result | Actual Behavior |
|------------|------------|-----------------|
| Find entry point | ❌ No Main() found | `UpdateService<T>.StartAsync()` |
| Trace service lifecycle | ❌ Can't follow reflection | Initialize → StartAsync → Register → StopAsync |
| Discover API routes | ❌ Static analysis misses dynamic routes | ARM-style URLs via custom provider |
| Find callers | ❌ Cross-assembly refs hidden | Service Fabric runtime invocation |

### Questions We Need to Answer

A complete repository understanding system should answer:

1. **Entry Points**: "Where does execution start?"
2. **Data Flow**: "How does data flow from API to database?"
3. **Dependencies**: "What external services does this depend on?"
4. **Security**: "Where is authentication enforced?"
5. **Errors**: "What exceptions can this throw?"
6. **Configuration**: "What settings does this service use?"
7. **Observability**: "What is logged/metered/traced?"
8. **Domain Model**: "What are the core business concepts?"

---

## Lessons from OpenCode

Analysis of OpenCode session logs reveals four successful strategies that LSP misses:

### 1. Pattern-Based Discovery

OpenCode searches for known hosting patterns:
```
Search patterns:
- "StatefulService" / "StatelessService"
- "IWebApiService" / "ServiceFabricHost"
- "IIntegrationTestService" / "Initialize"
- Assembly loading patterns (Activator.CreateInstance, Assembly.Load)
```

### 2. Configuration File Analysis

OpenCode parses Service Fabric manifests:
```
Files analyzed:
- ServiceManifest.xml → <ServiceTypes>, <CodePackage>
- Settings.xml → ServiceAssembly, ServiceType entries
- ApplicationManifest.xml → Service instances
- *.csproj → PackageReference for framework detection
```

### 3. Inheritance Tracing

OpenCode follows interface hierarchies:
```
IWebApiService<T>
    └── UpdateService<TDependency>
            └── Implements: Initialize(), StartAsync(), Register(), StopAsync()
                    └── Register() → OwinConfiguration → Controllers
```

### 4. Explicit Filtering

OpenCode excludes false positives:
```
Exclusions:
- Test assemblies (*Tests.dll, *Test.dll)
- Mock implementations (MockService*, FakeService*)
- Abstract base classes
- Interfaces without concrete implementations
```

---

## Repository Understanding Framework

### Core Philosophy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  REPOSITORY UNDERSTANDING = Extracting & Connecting Relationships          │
│                                                                             │
│  Code is a graph of:                                                        │
│    • Entities (classes, functions, endpoints, models, configs)              │
│    • Relationships (calls, inherits, implements, uses, depends-on)          │
│    • Behaviors (handles errors, logs, authenticates, caches)                │
│                                                                             │
│  Understanding = Answering questions like:                                  │
│    • "How does data flow through this service?"                             │
│    • "What external systems does this depend on?"                           │
│    • "Where is authentication enforced?"                                    │
│    • "What happens when this API is called?"                                │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Relationship Taxonomy

#### Category 1: Structural Relationships (Static Analysis)

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Inheritance** | Class extends/inherits from another | AST: base class parsing | `UpdateService : ServiceBase` |
| **Implementation** | Class implements interface | AST: interface list parsing | `class Foo : IRepository` |
| **Composition** | Class contains instance of another | AST: field/property types | `private readonly ILogger _logger` |
| **Dependency Injection** | Constructor/method parameter types | AST: constructor params | `public Foo(IBar bar)` |
| **Namespace Membership** | Type belongs to namespace | AST: namespace declaration | `namespace Company.Product.Module` |
| **Assembly Reference** | Project references another | MSBuild: ProjectReference | `<ProjectReference Include="..." />` |
| **Package Dependency** | Project uses NuGet package | MSBuild: PackageReference | `<PackageReference Include="Newtonsoft.Json" />` |

#### Category 2: Behavioral Relationships (Runtime Semantics)

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Method Invocation** | Code calls another method | AST: invocation expressions | `_service.ProcessAsync()` |
| **Event Publishing** | Code raises/publishes events | AST: event invocation patterns | `OnDataChanged?.Invoke()` |
| **Event Subscription** | Code subscribes to events | AST: event registration | `button.Click += Handler` |
| **Async/Await Flow** | Async method chains | AST: await expressions | `await FetchDataAsync()` |
| **Exception Flow** | Try/catch/throw patterns | AST: exception handling | `throw new ValidationException()` |

#### Category 3: API & Communication Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **HTTP Endpoint** | Exposed REST/Web API | AST: route attributes | `[HttpGet("api/users/{id}")]` |
| **HTTP Client Call** | Outbound HTTP request | AST: HttpClient usage | `_httpClient.GetAsync(url)` |
| **gRPC Service** | gRPC service definition | Proto files + generated code | `service Greeter { rpc SayHello }` |
| **gRPC Client Call** | Outbound gRPC request | AST: client stub usage | `_client.SayHelloAsync()` |
| **Message Queue Publish** | Publishes to queue/topic | AST: queue client patterns | `_bus.PublishAsync(message)` |
| **Message Queue Subscribe** | Consumes from queue/topic | AST: handler registration | `[MessageHandler("queue")]` |
| **SignalR Hub** | Real-time hub endpoint | AST: Hub inheritance | `class ChatHub : Hub` |
| **WebSocket** | WebSocket connections | AST: WebSocket patterns | `_socket.SendAsync()` |

#### Category 4: Data & Persistence Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Database Entity** | ORM entity/table mapping | AST: entity attributes | `[Table("Users")]` |
| **Database Query** | Database read operation | AST: query patterns | `_context.Users.Where(...)` |
| **Database Mutation** | Database write operation | AST: mutation patterns | `_context.SaveChangesAsync()` |
| **Repository Pattern** | Data access abstraction | AST: repository interfaces | `IUserRepository` |
| **Cache Read** | Cache retrieval | AST: cache client patterns | `_cache.GetAsync(key)` |
| **Cache Write** | Cache storage | AST: cache client patterns | `_cache.SetAsync(key, value)` |
| **File Read** | File system read | AST: File/Stream usage | `File.ReadAllTextAsync(path)` |
| **File Write** | File system write | AST: File/Stream usage | `File.WriteAllTextAsync(path)` |
| **Blob Storage** | Cloud blob operations | AST: blob client patterns | `_blobClient.UploadAsync()` |

#### Category 5: Security & Authentication Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Auth Required** | Endpoint requires authentication | AST: auth attributes | `[Authorize]` |
| **Role Required** | Endpoint requires specific role | AST: role attributes | `[Authorize(Roles = "Admin")]` |
| **Policy Required** | Endpoint requires policy | AST: policy attributes | `[Authorize(Policy = "CanEdit")]` |
| **Auth Bypass** | Endpoint allows anonymous | AST: anonymous attribute | `[AllowAnonymous]` |
| **Secret Access** | Code accesses secrets | AST: secret patterns | `_secretClient.GetSecret()` |
| **Certificate Usage** | Code uses certificates | AST: X509 patterns | `X509Certificate2.Load()` |
| **Token Validation** | JWT/token validation | AST: token handler patterns | `_tokenHandler.ValidateToken()` |

#### Category 6: Observability Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Log Statement** | Code writes log entry | AST: logger invocations | `_logger.LogInformation(...)` |
| **Metric Emission** | Code emits metric | AST: metrics patterns | `_metrics.RecordValue(...)` |
| **Trace Span** | Code creates trace span | AST: tracing patterns | `using var span = _tracer.StartSpan()` |
| **Health Check** | Health check registration | AST: health check patterns | `services.AddHealthChecks()` |
| **Audit Event** | Security/audit logging | AST: audit patterns | `_auditLog.Record(...)` |

#### Category 7: Configuration Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Config Binding** | Code binds to config section | AST: IOptions patterns | `IOptions<MyConfig>` |
| **Config Read** | Direct config access | AST: IConfiguration usage | `_config["ConnectionString"]` |
| **Environment Variable** | Env var access | AST: Environment.GetVar | `Environment.GetEnvironmentVariable()` |
| **Feature Flag** | Feature flag check | AST: feature flag patterns | `_featureManager.IsEnabled()` |
| **App Setting** | App settings access | Config file parsing | `appsettings.json` |

#### Category 8: Lifecycle & Scheduling Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Startup Hook** | App startup logic | AST: IHostedService patterns | `StartAsync()` |
| **Shutdown Hook** | App shutdown logic | AST: disposal patterns | `StopAsync()`, `Dispose()` |
| **Scheduled Job** | Timer/cron-based execution | AST: timer/scheduler patterns | `[CronSchedule("0 * * * *")]` |
| **Background Task** | Background processing | AST: BackgroundService | `ExecuteAsync()` |
| **Middleware** | Request pipeline component | AST: middleware patterns | `app.UseMiddleware<T>()` |

#### Category 9: Error Handling Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Exception Thrown** | Code throws exception | AST: throw statements | `throw new NotFoundException()` |
| **Exception Caught** | Code catches exception | AST: catch clauses | `catch (SqlException ex)` |
| **Error Response** | API error response | AST: problem details | `return Problem(...)` |
| **Retry Policy** | Retry/resilience logic | AST: Polly patterns | `Policy.Handle<Exception>().Retry()` |
| **Circuit Breaker** | Circuit breaker pattern | AST: circuit breaker patterns | `Policy.CircuitBreaker()` |

#### Category 10: Domain Model Relationships

| Relationship Type | Description | Detection Method | Example |
|-------------------|-------------|------------------|---------|
| **Aggregate Root** | DDD aggregate root | AST: marker interfaces | `class Order : AggregateRoot` |
| **Entity** | Domain entity | AST: entity patterns | `class OrderItem : Entity` |
| **Value Object** | Immutable value type | AST: value object patterns | `record Money(decimal Amount)` |
| **Domain Event** | Domain event type | AST: event patterns | `class OrderCreated : DomainEvent` |
| **Command** | CQRS command | AST: command patterns | `class CreateOrderCommand : ICommand` |
| **Query** | CQRS query | AST: query patterns | `class GetOrderQuery : IQuery<Order>` |

---

## Success Criteria

### Functional Requirements

| Requirement | Metric | Target |
|-------------|--------|--------|
| Entry point detection accuracy | Precision | > 90% |
| API endpoint discovery | Recall | > 95% |
| Relationship extraction | Categories covered | 10 |
| False positive rate | Entry points | < 10% |
| Configuration parsing | File types supported | 5+ |
| Framework support | Frameworks recognized | 15+ |

### Supported Frameworks & Patterns

**Hosting Frameworks:**
- [ ] ASP.NET Core (Web API, MVC, Minimal APIs)
- [ ] Service Fabric (Stateless, Stateful)
- [ ] Azure Functions
- [ ] Worker Services / BackgroundService
- [ ] gRPC Services
- [ ] SignalR Hubs
- [ ] OData Endpoints
- [ ] ARM Resource Provider APIs
- [ ] WinSvcHost (Windows Service via manifest loading)
- [ ] InProcHost (Test/Debug hosted services)
- [ ] FailoverCluster Services

**Data Access Patterns:**
- [ ] Entity Framework Core
- [ ] Dapper
- [ ] ADO.NET
- [ ] Cosmos DB SDK
- [ ] Redis / StackExchange.Redis

**Messaging Patterns:**
- [ ] Azure Service Bus
- [ ] RabbitMQ / MassTransit
- [ ] MediatR
- [ ] Azure Event Hubs
- [ ] Kafka

**HTTP Client Patterns:**
- [ ] HttpClient / IHttpClientFactory
- [ ] Refit
- [ ] RestSharp
- [ ] gRPC Client

**Observability Patterns:**
- [ ] ILogger / Microsoft.Extensions.Logging
- [ ] Application Insights
- [ ] OpenTelemetry
- [ ] Prometheus

**Resilience Patterns:**
- [ ] Polly
- [ ] Microsoft.Extensions.Resilience

### Relationship Categories Supported

| Category | Relationship Types | Status |
|----------|-------------------|--------|
| Structural | Inheritance, Composition, DI | [ ] |
| API | HTTP Endpoints, gRPC, GraphQL | [ ] |
| Data | Database, Cache, File I/O | [ ] |
| Messaging | Publish, Subscribe, Commands | [ ] |
| Security | Auth, Roles, Secrets | [ ] |
| Observability | Logging, Metrics, Tracing | [ ] |
| Configuration | Bindings, Feature Flags | [ ] |
| Lifecycle | Startup, Shutdown, Scheduled | [ ] |
| Error Handling | Exceptions, Retry, Circuit Breaker | [ ] |
| Domain | Aggregates, Events, Commands | [ ] |

### Performance Requirements

| Operation | Target |
|-----------|--------|
| Entry point discovery (medium repo) | < 5 seconds |
| Full endpoint discovery | < 10 seconds |
| Full relationship extraction | < 60 seconds |
| Incremental update (single file) | < 500ms |
| Graph query (simple) | < 100ms |
| Graph query (traversal) | < 500ms |

### Query Capabilities

The system should support answering these questions:

| Question | Query Type |
|----------|------------|
| "What external services does this depend on?" | Dependency traversal |
| "How does data flow from this API?" | Path finding |
| "Where is authentication enforced?" | Filter + aggregation |
| "What happens when this API is called?" | Execution trace |
| "What are the domain concepts?" | Category extraction |
| "Generate architecture summary" | Full graph analysis |

---

## References

- [Implementation Plan](./code_intelligence_implementation_plan.md)
- [AST Implementation Plan](./AST_IMPLEMENTATION_PLAN.md)
- [LSP Migration Plan](./LSP_MIGRATION_PLAN.md)
- [Embedding Model Approach](./embedding_model_approach.md)
- [Code Understanding Comparison](./code_understanding_comparison.md)
