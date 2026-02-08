Analyze this repository at the **feature level**, not the project level. Identify and describe each distinct functional capability or cross-cutting concern. For each feature discovered, provide:

1. **Feature name** and a one-line summary
2. **Where it lives** — the files, folders, and namespaces that implement it
3. **How it works** — key classes, interfaces, patterns, and data flow
4. **Providers / Handlers / Strategies** — enumerate every concrete implementation, adapter, or plugin registered for this feature

Specifically look for these feature categories (and any others present):

- **Storage / Persistence layer** — databases, file stores, caches, serialization formats, repository classes
- **Update / Discovery** — how entities, configurations, or resources are discovered and refreshed; include all providers, handlers, scanners, and watchers
- **Security** — authentication, authorization, secrets management, token handling, permission models
- **Messaging / Eventing** — event buses, queues, pub/sub, notification pipelines
- **Logging / Telemetry** — structured logging, tracing, metrics, diagnostic sinks
- **Configuration** — how settings are loaded, layered, and bound; config providers and sources
- **Scheduling / Orchestration** — job runners, workflow engines, cron triggers, retry policies
- **Background jobs** — hosted services, long-running tasks, worker loops, queue consumers, periodic cleanup or sync jobs
- **API endpoints / Routing** — all exposed HTTP endpoints (REST, gRPC, minimal APIs); for each endpoint include: route template, HTTP method, controller/handler method, request parameters (query, path, body with types), response structure (status codes, return types/DTOs), and authentication/authorization requirements
- **External integrations** — third-party APIs, SDK clients, REST/gRPC consumers

For each feature, also list:
- Dependencies on other features within the repo
- External NuGet / npm packages it relies on
- Extension points where new providers or handlers can be added

Finish with a **feature dependency graph** showing how these features relate to each other.