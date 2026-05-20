# Clean Code Metrics -- Architecture

> **Story**: code-intelligence:CLEAN-CODE  
> **Points**: 13  
> **Status**: Design

---

## 1. Problem Statement

Large repositories and cross-repo ecosystems lack measurable, automated enforcement
of clean code standards. Existing analyzers (e.g., `ast-metrics`, SonarQube) compute
per-file metrics but do not map them onto SOLID principles or decoupled functional
areas at the system level. This story delivers:

1. **System-level metrics** -- quantitative scores for coupling, cohesion, SOLID
   adherence, and architectural layering that work across large repos and multi-repo
   graphs.
2. **Evaluator integration** -- an evaluator agent prompt consumes metric snapshots
   to gate pull requests and flag regressions.
3. **Refactoring prioritization** -- ranked hotspot reports that drive remediation
   effort in big repos.

---

## 2. Component Overview

```
+----------------------------------------------------------------------+
|                         Ast.Cli (existing)                            |
|  Commands: parse, enrich, embed, index, query, stats, [NEW] metrics  |
+----------------------------------------------------------------------+
        |                          |                         |
        v                          v                         v
+------------------+  +------------------------+  +-------------------+
| Ast.Core         |  | CodeIntelligence       |  | Ast.GraphDb       |
| HypergraphModels |  | Analyzers / Strategies |  | IGraphStore       |
| RepositoryProc.  |  | Models / Rules / Scor. |  | Neo4j / Queries   |
+------------------+  +------------------------+  +-------------------+
        |                     |        |
        v                     v        v
+------------------+  +---------------------------+
| [NEW]            |  | [NEW]                     |
| CleanCode.       |  | CleanCode.Evaluator       |
| Metrics          |  | (Prompt contract + SDK)   |
+------------------+  +---------------------------+
        |                     |
        v                     v
+--------------------------------------------------+
| [NEW] CleanCode.Reports                          |
| Dashboard JSON / Markdown / Sarif output         |
+--------------------------------------------------+
```

### 2.1 New Components

| Component | Assembly | Responsibility |
|-----------|----------|----------------|
| **CleanCode.Metrics** | `src/ast/CleanCode.Metrics` | Compute SOLID and coupling metrics from the hypergraph and AST data. Stateless calculators. |
| **CleanCode.Metrics / MetricOrchestrator** | `src/ast/CleanCode.Metrics` | Coordinates all `IMetricCalculator` instances, collects results, invokes aggregation, and assembles the final `MetricSnapshot`. |
| **CleanCode.Metrics / DeltaCalculator** | `src/ast/CleanCode.Metrics` | Compares two `MetricSnapshot` instances (baseline vs. current) and produces per-metric regression/improvement deltas for PR gates. |
| **CleanCode.Metrics / HotspotRanker** | `src/ast/CleanCode.Metrics` | Ranks classes/namespaces by a weighted composite of violation severity, change frequency, blast radius, and coupling*complexity. |
| **CleanCode.Evaluator** | `src/ast/CleanCode.Evaluator` | Evaluator prompt contract: accepts a `MetricSnapshot`, returns `EvaluationVerdict` with pass/warn/fail and remediation hints. Invoked as a CLI subprocess (not inside Forge agent loop). |
| **CleanCode.Reports** | `src/ast/CleanCode.Reports` | Renders metric results into SARIF, JSON dashboard payloads, and Markdown summaries for CI gates. |
| **CleanCode.Reports / DashboardExporter** | `src/ast/CleanCode.Reports` | Produces the JSON payload consumed by external dashboard systems (Grafana, custom UIs). This component owns the dashboard contract; the visualization tool is external and not part of this system. |

### 2.2 Existing Components (Extended)

| Component | Extension |
|-----------|-----------|
| `CodeIntelligence.Analyzers` | `ArchitectureMetricsCalculator` gains new metric delegates; `LayerViolationDetector` feeds violations into metric snapshots. |
| `Ast.Cli` | New `metrics` command orchestrates analysis and outputs reports. |
| `Ast.GraphDb` | Cypher queries for cross-repo dependency depth and blast radius. |
| `Ast.Core` | `HypergraphNode.Metadata` extended with method-level complexity tags. |

---

## 3. Data Model

### 3.1 Core Entities

#### MetricSnapshot

Top-level aggregate capturing one measurement pass over a repository (or multi-repo set).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | GUID |
| `Timestamp` | `DateTimeOffset` | When the snapshot was taken |
| `RepositoryId` | `string` | Repository identifier (path or remote URL) |
| `CommitSha` | `string?` | Git SHA at measurement time |
| `Metrics` | `IReadOnlyList<MetricValue>` | All computed metric values |
| `Summary` | `ScoreSummary` | Rolled-up pass/warn/fail |

#### ScoreSummary

Rolled-up quality gate result for the entire snapshot.

| Field | Type | Description |
|-------|------|-------------|
| `Outcome` | `VerdictOutcome` | Enum: `Pass`, `Warn`, `Fail` |
| `PassCount` | `int` | Metrics within pass threshold |
| `WarnCount` | `int` | Metrics in warning zone |
| `FailCount` | `int` | Metrics exceeding fail threshold |
| `CompositeScore` | `double` | Weighted 0.0-1.0 overall score |
| `TopViolations` | `IReadOnlyList<Violation>` | Worst N violations for quick summary |

#### MetricValue

Single metric measurement.

| Field | Type | Description |
|-------|------|-------------|
| `MetricId` | `string` | e.g. `solid.srp`, `coupling.afferent` |
| `Category` | `MetricCategory` | Enum: `Coupling`, `Cohesion`, `SOLID`, `Complexity`, `Architecture`, `CrossRepo` |
| `Scope` | `MetricScope` | Enum: `Method`, `Class`, `Namespace`, `Project`, `Repository`, `System` |
| `Value` | `double` | Numeric result |
| `Threshold` | `MetricThreshold` | Pass/warn/fail bounds |
| `Target` | `string` | Fully-qualified name of the measured element |
| `Details` | `IReadOnlyDictionary<string, object>?` | Extra context |

#### MetricThreshold

| Field | Type | Description |
|-------|------|-------------|
| `PassBelow` | `double?` | Value at or below this passes (for "lower is better" metrics) |
| `PassAbove` | `double?` | Value at or above this passes (for "higher is better" metrics) |
| `WarnBelow` | `double?` | Warn boundary |
| `WarnAbove` | `double?` | Warn boundary |

#### EvaluationVerdict

Returned by the evaluator agent after inspecting a snapshot.

| Field | Type | Description |
|-------|------|-------------|
| `Outcome` | `VerdictOutcome` | Enum: `Pass`, `Warn`, `Fail` |
| `Violations` | `IReadOnlyList<Violation>` | Individual issues |
| `RemediationHints` | `IReadOnlyList<string>` | Actionable suggestions |
| `OverallScore` | `double` | 0.0 - 1.0 composite |

#### Violation

| Field | Type | Description |
|-------|------|-------------|
| `RuleId` | `string` | e.g. `SOLID-DIP-001` |
| `Severity` | `ViolationSeverity` | `Critical`, `High`, `Medium`, `Low` |
| `Target` | `string` | Fully-qualified name of offending element |
| `Message` | `string` | Human-readable explanation |
| `MetricId` | `string` | The metric that triggered this |
| `ActualValue` | `double` | Measured value |
| `ThresholdValue` | `double` | Expected bound |

#### SystemMetricSnapshot

Aggregates multiple per-repository snapshots into a system-wide measurement (used in cross-repo scenarios).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | GUID |
| `Timestamp` | `DateTimeOffset` | When the system snapshot was assembled |
| `RepositorySnapshots` | `IReadOnlyList<MetricSnapshot>` | Per-repo snapshots included |
| `SystemMetrics` | `IReadOnlyList<MetricValue>` | Cross-repo metrics (depth, duplication, knowledge dist.) |
| `Summary` | `ScoreSummary` | System-wide rolled-up score |

#### MetricDelta

Result of comparing a current snapshot to a baseline (used by `DeltaCalculator`).

| Field | Type | Description |
|-------|------|-------------|
| `MetricId` | `string` | Which metric changed |
| `BaselineValue` | `double` | Value on baseline branch |
| `CurrentValue` | `double` | Value on PR branch |
| `Delta` | `double` | CurrentValue - BaselineValue |
| `Regressed` | `bool` | True if delta crosses a threshold boundary |

#### HotspotEntry

Single entry in the ranked hotspot report (produced by `HotspotRanker`).

| Field | Type | Description |
|-------|------|-------------|
| `Target` | `string` | Fully-qualified name of class/namespace |
| `FilePath` | `string` | Primary source file |
| `CompositeScore` | `double` | Weighted severity score (higher = worse) |
| `ViolatedMetrics` | `IReadOnlyList<string>` | Metric IDs that failed |
| `ChangeFrequency` | `int` | Commits touching this element in last 90 days |
| `BlastRadius` | `int` | Number of dependent components |
| `SuggestedRefactoring` | `string` | Pattern name (e.g., "Extract Interface", "Split Class") |

### 3.2 Metric Catalog

Metrics are grouped into six categories aligned with the story goals.

#### Category: SOLID

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `solid.srp` | Single Responsibility | Class | Ratio of responsibilities (method clusters) to 1. Lower is better. | <= 1.5 |
| `solid.ocp` | Open/Closed | Class | Ratio of virtual/abstract extension points to concrete modifications in recent commits. | >= 0.6 |
| `solid.lsp` | Liskov Substitution | Class | Derived types that override and change preconditions. Count of violations. | == 0 |
| `solid.isp` | Interface Segregation | Interface | Average number of members per interface. Lower is better. | <= 7 |
| `solid.dip` | Dependency Inversion | Namespace | Ratio of dependencies on abstractions vs. concretions. Higher is better. | >= 0.7 |

#### Category: Coupling

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `coupling.afferent` | Afferent Coupling (Ca) | Class/Namespace | Incoming dependencies count. | <= 20 |
| `coupling.efferent` | Efferent Coupling (Ce) | Class/Namespace | Outgoing dependencies count. | <= 15 |
| `coupling.instability` | Instability (I) | Namespace | Ce / (Ca + Ce). Balanced is good. | 0.3 - 0.7 |
| `coupling.abstractness` | Abstractness (A) | Namespace | Abstract types / total types. | 0.2 - 0.8 |
| `coupling.distance` | Distance from Main Seq. | Namespace | abs(A + I - 1). Lower is better. | <= 0.3 |
| `coupling.cross_repo_depth` | Cross-Repo Dep. Depth | System | Longest chain of repo-to-repo references. | <= 3 |

#### Category: Cohesion

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `cohesion.lcom4` | LCOM4 | Class | Lack of Cohesion of Methods (graph components). | == 1 |
| `cohesion.tcc` | Tight Class Cohesion | Class | Ratio of directly-connected method pairs. Higher is better. | >= 0.5 |

#### Category: Complexity

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `complexity.cyclomatic` | Cyclomatic Complexity | Method | McCabe's complexity. | <= 10 |
| `complexity.cognitive` | Cognitive Complexity | Method | SonarSource cognitive complexity. | <= 15 |
| `complexity.class_wmc` | Weighted Methods/Class | Class | Sum of method complexities. | <= 50 |

#### Category: Architecture

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `arch.layer_adherence` | Layer Adherence | Project | % of interactions that follow allowed layer rules. | >= 0.95 |
| `arch.blast_radius` | Blast Radius | Class | Services that could break on change. | <= 3 |
| `arch.debt_ratio` | Architectural Debt Ratio | Repository | Remediation effort / dev effort. | <= 0.15 |

#### Category: CrossRepo

| Metric ID | Name | Scope | Description | Pass Threshold |
|-----------|------|-------|-------------|----------------|
| `crossrepo.duplication` | Cross-Repo Duplication | System | Duplicate code patterns across repos. | <= 0.02 |
| `crossrepo.knowledge_dist` | Knowledge Distribution | System | Unique maintainers / critical components. | >= 3 |

---

## 4. Interfaces

### 4.1 IMetricCalculator

Primary interface for all metric calculators (one per metric or metric group).

```csharp
namespace CleanCode.Metrics;

public interface IMetricCalculator
{
    string MetricId { get; }
    MetricCategory Category { get; }
    MetricScope DefaultScope { get; }

    Task<IReadOnlyList<MetricValue>> CalculateAsync(
        MetricContext context,
        CancellationToken ct = default);
}
```

`MetricContext` wraps the hypergraph, optional Roslyn workspace, and configuration:

```csharp
public record MetricContext
{
    public required HypergraphData Hypergraph { get; init; }
    public IGraphStore? GraphStore { get; init; }
    public Workspace? RoslynWorkspace { get; init; }
    public required MetricConfiguration Config { get; init; }
}
```

`MetricConfiguration` maps to the `.cleancode.yaml` schema (section 7) and controls
which metrics are computed and their thresholds:

```csharp
public record MetricConfiguration
{
    public required string PolicyId { get; init; }
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];
    public bool IncludeTests { get; init; } = false;
    public IReadOnlyList<string> Languages { get; init; } = ["csharp"];
    public IReadOnlyDictionary<string, MetricThreshold> ThresholdOverrides { get; init; }
        = new Dictionary<string, MetricThreshold>();
    public string BaselineBranch { get; init; } = "main";
    public IReadOnlyList<string> ReportFormats { get; init; } = ["sarif"];
}
```

### 4.2 IMetricAggregator

Rolls up method/class-level metrics to namespace/project/repo/system scope.

```csharp
namespace CleanCode.Metrics;

public interface IMetricAggregator
{
    MetricValue Aggregate(
        IReadOnlyList<MetricValue> values,
        MetricScope targetScope,
        AggregationStrategy strategy);
}

public enum AggregationStrategy
{
    Mean,
    Median,
    P95,
    Max,
    Sum,
    WeightedByLoc
}
```

### 4.3 IMetricOrchestrator

Coordinates the full metrics pipeline: discovery of calculators, execution, aggregation, snapshot assembly.

```csharp
namespace CleanCode.Metrics;

public interface IMetricOrchestrator
{
    Task<MetricSnapshot> RunAsync(
        MetricContext context,
        CancellationToken ct = default);

    Task<MetricSnapshot> RunIncrementalAsync(
        MetricContext context,
        IReadOnlyList<string> changedFiles,
        MetricSnapshot baseline,
        CancellationToken ct = default);
}
```

### 4.4 IDeltaCalculator

Compares two snapshots and produces per-metric deltas for PR gate decisions.

```csharp
namespace CleanCode.Metrics;

public interface IDeltaCalculator
{
    IReadOnlyList<MetricDelta> ComputeDeltas(
        MetricSnapshot baseline,
        MetricSnapshot current);

    bool HasRegression(
        IReadOnlyList<MetricDelta> deltas,
        EvaluatorPolicy policy);
}
```

### 4.5 IHotspotRanker

Produces a prioritized list of refactoring targets.

```csharp
namespace CleanCode.Metrics;

public interface IHotspotRanker
{
    Task<IReadOnlyList<HotspotEntry>> RankAsync(
        MetricSnapshot snapshot,
        HotspotOptions options,
        CancellationToken ct = default);
}

public record HotspotOptions
{
    public int TopN { get; init; } = 20;
    public double SeverityWeight { get; init; } = 0.4;
    public double ChangeFrequencyWeight { get; init; } = 0.2;
    public double BlastRadiusWeight { get; init; } = 0.2;
    public double CouplingComplexityWeight { get; init; } = 0.2;
}
```

### 4.6 IEvaluatorContract

Contract between the metrics system and the evaluator agent prompt.

```csharp
namespace CleanCode.Evaluator;

public interface IEvaluatorContract
{
    Task<EvaluationVerdict> EvaluateAsync(
        MetricSnapshot snapshot,
        EvaluatorPolicy policy,
        CancellationToken ct = default);
}
```

`EvaluatorPolicy` defines which metrics are enforced, which are advisory, and
custom thresholds overriding defaults:

```csharp
public record EvaluatorPolicy
{
    public required string PolicyId { get; init; }
    public IReadOnlyList<MetricEnforcement> Enforcements { get; init; } = [];
    public IReadOnlyDictionary<string, MetricThreshold>? ThresholdOverrides { get; init; }
}

public record MetricEnforcement
{
    public required string MetricId { get; init; }
    public required EnforcementLevel Level { get; init; }
}

public enum EnforcementLevel { Blocking, Advisory, Informational }
```

### 4.7 IReportRenderer

Renders evaluation results to various output formats.

```csharp
namespace CleanCode.Reports;

public interface IReportRenderer
{
    string Format { get; }
    Task<Stream> RenderAsync(
        MetricSnapshot snapshot,
        EvaluationVerdict verdict,
        RenderOptions options,
        CancellationToken ct = default);
}
```

Implementations: `SarifRenderer`, `DashboardExporter`, `MarkdownRenderer`.

`RenderOptions` controls output formatting:

```csharp
public record RenderOptions
{
    public string? OutputPath { get; init; }
    public bool IncludePassingMetrics { get; init; } = false;
    public int MaxViolationsPerFile { get; init; } = 50;
    public bool IncludeRemediationHints { get; init; } = true;
    public string? DashboardSchemaVersion { get; init; }
}
```

### 4.8 Cross-Repo Query Interface (extends Ast.GraphDb)

```csharp
namespace Ast.GraphDb;

public interface ICrossRepoQueryService
{
    Task<int> GetDependencyDepthAsync(
        string repositoryId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetBlastRadiusAsync(
        string nodeId, CancellationToken ct = default);

    Task<IReadOnlyList<DuplicationMatch>> FindCrossRepoDuplicatesAsync(
        string pattern, float similarityThreshold = 0.9f,
        CancellationToken ct = default);
}
```

---

## 5. End-to-End Sequence Flows

### 5.1 Scenario: Full Repository Metrics Scan

```
Actor: Developer (via CLI)
Command: ast-cli metrics --repo . --output report.sarif

1. Ast.Cli.MetricsCommand parses args, resolves repo path.
2. Ast.Core.RepositoryProcessor builds HypergraphData (existing).
3. MetricsCommand creates MetricContext (hypergraph + config).
4. MetricOrchestrator iterates registered IMetricCalculator instances:
   a. CouplingCalculator -- walks hypergraph edges for Ca/Ce/I/A/D.
   b. CohesionCalculator -- builds method-connectivity graph per class.
   c. SolidCalculator -- delegates to sub-calculators (SRP, OCP, etc.).
   d. ComplexityCalculator -- uses Roslyn syntax trees for CC/Cognitive.
   e. ArchitectureCalculator -- calls existing LayerViolationDetector.
5. MetricAggregator rolls up class-level values to namespace/project/repo.
6. MetricOrchestrator assembles MetricSnapshot.
7. EvaluatorContract.EvaluateAsync(snapshot, policy) returns EvaluationVerdict.
8. SarifRenderer.RenderAsync produces report.sarif to disk.
9. CLI prints summary table (pass/warn/fail counts).
```

### 5.2 Scenario: Evaluator Agent Guards a PR

```
Actor: CI Pipeline / Evaluator Agent

1. CI triggers on PR create/update.
2. CI invokes: ast-cli metrics --repo . --diff HEAD~1
   (incremental mode: only re-analyzes changed files).
3. MetricsCommand loads prior MetricSnapshot from baseline via git notes.
4. DeltaCalculator computes per-metric diff (new value vs. baseline).
5. MetricsCommand writes snapshot to disk (e.g., .metrics/current.json).
6. CI invokes: ast-cli evaluate --snapshot .metrics/current.json --policy .cleancode.yaml
   (standalone subprocess per section 10.2).
7. EvaluatorContract loads snapshot + policy, calls LLM endpoint for judgment.
8. Evaluator prompt evaluates:
   - Any metric crossing from pass to warn/fail -> flag.
   - Net regression > 5% in composite score -> block.
9. EvaluationVerdict returned with Violations list.
10. CI posts violations as PR comments (SARIF integration).
11. If verdict.Outcome == Fail, CI marks check as failed.
```

### 5.3 Scenario: Cross-Repo System Measurement

```
Actor: Platform team (via scheduled job)

1. Orchestrator enumerates registered repositories (config file).
2. For each repo:
   a. Clone/update to latest commit.
   b. Run ast-cli metrics --repo <path> --output <repo-id>.json
   c. Push MetricSnapshot to central Neo4j graph store.
3. CrossRepoQueryService computes system-level metrics:
   a. cross_repo_depth via graph traversal (longest path).
   b. cross_repo_duplication via vector similarity (Qdrant).
   c. knowledge_distribution via git blame aggregation.
4. SystemMetricSnapshot assembled (aggregates all repo snapshots).
5. DashboardExporter produces JSON payload to configured output path.
6. External dashboard system reads payload for visualization (see section 10.3).
```

### 5.4 Scenario: Refactoring Prioritization

```
Actor: Tech Lead (via CLI or dashboard)

1. ast-cli metrics --repo . --hotspots --top 20
2. MetricsCommand runs full scan.
3. HotspotRanker scores each class/namespace by:
   a. Composite metric violation severity (weighted).
   b. Change frequency (git log --stat).
   c. Blast radius (graph query).
   d. Coupling * complexity product.
4. Output: ranked list with estimated remediation effort per item.
5. Each hotspot includes: location, violated metrics, suggested refactoring pattern.
```

---

## 6. Integration Points

### 6.1 With Existing CodeIntelligence Analyzers

The new `CleanCode.Metrics` calculators consume outputs from:

- `LayerClassificationAnalyzer` -- provides `LayerClassification` for architecture metrics.
- `LayerViolationDetector` -- provides `LayerViolation` for `arch.layer_adherence`.
- `ArchitectureMetricsCalculator` -- existing coupling/cohesion scores are wrapped as `MetricValue`.
- `InheritanceAnalyzer` -- hierarchy data for LSP (Liskov) violation detection.
- `DependencyInjectionAnalyzer` -- DI relationships for DIP metric.

### 6.2 With Ast.GraphDb (Neo4j)

- `ICrossRepoQueryService` executes Cypher queries for cross-repo dependency depth and blast radius.
- Metric snapshots can be stored as graph nodes (`(:MetricSnapshot)-[:MEASURED]->(:Repository)`) for historical trending.

### 6.3 With Ast.VectorDb (Qdrant)

- Cross-repo duplication detection uses embedding similarity search.
- Code clones identified by cosine similarity > 0.9 across repository partitions.

### 6.4 With Evaluator Agent (Forge)

- The evaluator agent receives `MetricSnapshot` as structured JSON in its prompt context.
- `EvaluatorPolicy` is loaded from `.cleancode.yaml` in the repository root.
- The agent returns `EvaluationVerdict` as structured JSON which the CLI/CI parses.

### 6.5 With CI/CD Pipeline

- SARIF output integrates with GitHub Advanced Security, Azure DevOps, and VS Code.
- Exit code 0 (pass), 1 (fail), 2 (warn-only) for CI gate decisions.
- Baseline snapshots stored in `refs/notes/metrics/<branch>` (see section 10.1).

---

## 7. Configuration Schema

Repository-level configuration in `.cleancode.yaml`:

```yaml
version: "1.0"
policy:
  id: "default"
  enforcement:
    - metricId: "solid.dip"
      level: blocking
    - metricId: "coupling.instability"
      level: advisory
    - metricId: "complexity.cyclomatic"
      level: blocking
  thresholdOverrides:
    "complexity.cyclomatic":
      passBelow: 15  # team prefers higher limit
    "coupling.afferent":
      passBelow: 30

scan:
  exclude:
    - "**/Generated/**"
    - "**/Migrations/**"
  includeTests: false
  languages:
    - csharp

crossRepo:
  enabled: true
  repositories:
    - id: "core-platform"
      url: "https://dev.azure.com/org/project/_git/core-platform"
    - id: "shared-libs"
      url: "https://dev.azure.com/org/project/_git/shared-libs"

reporting:
  formats:
    - sarif
    - json
    - markdown
  baselineBranch: "main"
```

---

## 8. Deployment Topology

```
+-----------+      +------------------+      +----------+
| Developer |----->| ast-cli metrics  |----->| Neo4j    |
| Workstation|     | (local scan)     |      | (opt.)   |
+-----------+      +------------------+      +----------+
                          |
                          v
                   +-------------+
                   | .sarif file |
                   +-------------+

+-----------+      +------------------+      +----------+
| CI Agent  |----->| ast-cli metrics  |----->| Qdrant   |
| (PR build)|     | --diff HEAD~1    |      | (opt.)   |
+-----------+      +------------------+      +----------+
                          |
                          v
                   +-------------------+
                   | Evaluator Agent   |
                   | (structured JSON) |
                   +-------------------+
                          |
                          v
                   +-------------------+
                   | PR Comment / Gate |
                   +-------------------+

+-----------+      +------------------+      +----------+
| Scheduled |----->| cross-repo scan  |----->| Neo4j +  |
| Job       |      | (all repos)      |      | Qdrant   |
+-----------+      +------------------+      +----------+
                          |
                          v
                   +-------------------+
                   | Dashboard JSON    |
                   +-------------------+
```

---

## 9. Design Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Separate `CleanCode.Metrics` from `CodeIntelligence` | Single Responsibility: metrics computation is distinct from relationship detection. Avoids bloating the existing analyzer assembly. |
| D2 | Evaluator as a prompt contract, not embedded logic | Decouples metric computation from judgment policy. Policies evolve independently. Allows LLM-based reasoning for nuanced cases. |
| D3 | SARIF as primary CI output | Industry standard for static analysis; integrates with GitHub, ADO, VS Code without custom adapters. |
| D4 | Metric IDs use dotted hierarchy | Enables wildcard policy rules (e.g., `solid.*` = blocking) and dashboard drill-down. |
| D5 | Incremental (diff) mode for PR checks | Full repo scan is too slow for PR feedback loops. Delta mode re-analyzes only changed files and recomputes affected metrics. |
| D6 | Neo4j for cross-repo queries | Already deployed for code intelligence graph. Cypher excels at path-length queries (dependency depth, blast radius). |
| D7 | Reuse `ArchitectureMetricsCalculator` | Existing calculator already computes coupling/cohesion/adherence. Wrap its output rather than duplicate logic. |

---

## 10. Architectural Decisions: Settled Boundaries

### 10.1 Baseline Storage (fixes item 4)

**Decision**: Baselines are stored in `refs/notes/metrics/<branch>` using git notes.

| Aspect | Detail |
|--------|--------|
| Storage mechanism | `git notes add -f --ref=metrics/main` attaches snapshot JSON to the commit |
| Retrieval | `git notes show --ref=metrics/main HEAD` retrieves the baseline for comparison |
| CI integration | `ast-cli metrics --diff` reads the note from the `baselineBranch` configured in `.cleancode.yaml` |
| Advantages | No extra branch clutter; travels with the repo; works offline |
| Trade-off | Notes are not cloned by default; CI must use `git fetch origin refs/notes/*:refs/notes/*` |

### 10.2 Evaluator Hosting (fixes item 5)

**Decision**: The evaluator runs as a **standalone CLI subprocess**, not inside the Forge agent loop.

| Aspect | Detail |
|--------|--------|
| Invocation | `ast-cli evaluate --snapshot <path>.json --policy .cleancode.yaml` |
| Runtime | `CleanCode.Evaluator` assembly loaded by `Ast.Cli`; calls an LLM endpoint for nuanced judgment |
| Why not Forge | Forge agent loops are long-running and PR-gate latency must be bounded (<60s). A subprocess starts fresh, evaluates, exits. |
| Interface boundary | `IEvaluatorContract` (section 4.6) is the sole integration surface. Forge may invoke the same CLI command in its own workflows but does not host the evaluator internally. |

### 10.3 Dashboard Boundary (fixes item 6)

**Decision**: The dashboard visualization tool is **external** (Grafana, Power BI, or custom UI). This system produces a well-defined JSON payload via `DashboardExporter` but does NOT own or deploy a dashboard service.

| Aspect | Detail |
|--------|--------|
| Producer | `CleanCode.Reports.DashboardExporter` implements `IReportRenderer` with `Format = "json-dashboard"` |
| Contract | Output JSON schema is versioned in `.cleancode.yaml` under `reporting.dashboardSchemaVersion` |
| Consumer | Any external system that reads the JSON (Grafana via JSON API plugin, custom React dashboard, etc.) |
| Delivery | The scheduled cross-repo job (section 5.3) writes JSON to a configured output path; the external dashboard reads from there. No runtime coupling. |

### 10.4 Language Scope

**Decision**: Initial scope is **C# only**. PowerShell metric support is deferred to a follow-up story. The configuration schema's `languages` field accepts only `csharp` for this story.
