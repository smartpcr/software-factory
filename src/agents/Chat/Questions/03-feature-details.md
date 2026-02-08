Given the feature **{feature}**, perform a thorough deep-dive analysis covering every aspect of how this feature is implemented in the repository. Organize your response into the following sections:

## Dependency Injection & Registration

- How is the feature's dependency injection set up? Show all DI registrations (`AddScoped`, `AddSingleton`, `AddTransient`, `services.Add*`, custom extension methods).
- Identify all factory classes, factory methods, and builder patterns used to construct the feature's components.
- List every configuration settings class (e.g., `IOptions<T>`, `IConfiguration` bindings) related to this feature and where they are registered.
- Show the full registration chain: from `Startup.cs` / `Program.cs` / `HostBuilder` through to the final resolved services.

## Implementation Across Layers

Be thorough and deep — this feature likely spans multiple areas of the codebase:

- **Configuration / Startup** — how the feature is initialized, configured, and wired up at application start
- **API / Controllers / Endpoints** — any HTTP endpoints, route handlers, gRPC services, or CLI commands that expose this feature
- **Business Logic / Domain** — core logic, domain models, services, validators, and rules
- **Persistence / Data Access** — repositories, DbContext mappings, migrations, query builders, stored procedures
- **Serialization / DTOs** — data transfer objects, serializers, mappers, format converters
- **Messaging / Events** — event publishers, subscribers, handlers, message contracts related to this feature
- **Background Processing** — hosted services, job handlers, queue consumers, periodic tasks
- **Cross-cutting Concerns** — logging, telemetry, caching, retry policies, circuit breakers specific to this feature

For each layer, list the specific files, classes, interfaces, and methods involved.

## Testing

- **Unit Tests** — which classes are unit-tested? What mocking strategies are used (Moq, stubs, fakes)? List the test classes and what they cover.
- **Integration Tests** — are there integration tests that exercise this feature end-to-end? What test infrastructure is used (TestServer, Kustainer, in-memory DB, BDD/Reqnroll)?
- **Test Coverage Gaps** — identify areas of this feature that appear to lack test coverage.

## Knowledge Graph

Generate a knowledge graph for this feature that can serve as context for a PR reviewer or subagent tasked with extending this feature. The graph should include:

- **Nodes**: every class, interface, configuration, endpoint, test class, and external dependency related to this feature
- **Edges**: relationships such as `implements`, `depends-on`, `configures`, `tests`, `calls`, `publishes`, `subscribes`, `persists-to`
- **Entry points**: where a developer would start to add new functionality to this feature
- **Extension points**: interfaces, abstract classes, or plugin points designed for adding new behavior
- **Invariants**: business rules or constraints that must be preserved when modifying this feature

Present the graph in Mermaid diagram syntax so it can be rendered visually, followed by a prose summary a subagent could use as system prompt context when working on PRs that touch this feature.