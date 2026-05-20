---
title: "clean code"
storyId: "code-intelligence:CLEAN-CODE"
---

# Phase 1: Foundation and Core Models

## Dependencies
- _none -- start phase_

## Stage 1.1: Project Scaffolding and Data Models

### Implementation Steps
- [ ] Create `src/ast/CleanCode.Metrics/CleanCode.Metrics.csproj` with .NET 8.0 target, win-x64 runtime identifier, and project references to `Ast.Core` and `CodeIntelligence`
- [ ] Create `src/ast/CleanCode.Evaluator/CleanCode.Evaluator.csproj` with project reference to `CleanCode.Metrics`
- [ ] Create `src/ast/CleanCode.Reports/CleanCode.Reports.csproj` with project reference to `CleanCode.Metrics` and `CleanCode.Evaluator`
- [ ] Add `Sarif.Sdk` package to `Directory.Packages.props` under the centrally managed packages section and add `<PackageReference Include="Sarif.Sdk" />` to `CleanCode.Reports.csproj`
- [ ] Add all three new projects to `Workflow.sln` under an `ast` solution folder
- [ ] Implement core data model classes in `CleanCode.Metrics/Models/`: `MetricSnapshot`, `MetricValue`, `MetricThreshold`, `ScoreSummary`, `MetricDelta`, `HotspotEntry`, `SystemMetricSnapshot`
- [ ] Implement enums: `MetricCategory` (Coupling, Cohesion, SOLID, Complexity, Architecture, CrossRepo), `MetricScope` (Method, Class, Namespace, Project, Repository, System), `VerdictOutcome` (Pass, Warn, Fail), `ViolationSeverity` (Critical, High, Medium, Low)
- [ ] Implement `Violation` and `EvaluationVerdict` models in `CleanCode.Evaluator/Models/`
- [ ] Implement `MetricContext` and `MetricConfiguration` records in `CleanCode.Metrics/`

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: MetricSnapshot serialization -- Given a populated MetricSnapshot with 5 MetricValues, When serialized to JSON and deserialized back, Then all fields round-trip correctly including nested Violations
- [ ] Scenario: MetricThreshold evaluation -- Given a MetricValue with value 12 and PassBelow threshold 10 and WarnBelow threshold 15, When ScoreSummary evaluates the value, Then Outcome is Warn
- [ ] Scenario: Project build -- Given all three new .csproj files added to the solution, When `dotnet build Workflow.sln` is invoked, Then the build succeeds with zero errors

## Stage 1.2: Core Interfaces and Configuration Schema

### Implementation Steps
- [ ] Implement `IMetricCalculator` interface in `CleanCode.Metrics/` with `MetricId`, `Category`, `DefaultScope`, and `CalculateAsync` method
- [ ] Implement `IMetricAggregator` interface with `Aggregate` method and `AggregationStrategy` enum (Mean, Median, P95, Max, Sum, WeightedByLoc)
- [ ] Implement `IMetricOrchestrator` interface with `RunAsync` and `RunIncrementalAsync` methods
- [ ] Implement `IDeltaCalculator` interface with `ComputeDeltas` and `HasRegression` methods
- [ ] Implement `IHotspotRanker` interface with `RankAsync` method and `HotspotOptions` record
- [ ] Implement `IEvaluatorContract` interface in `CleanCode.Evaluator/` with `EvaluateAsync` method and `EvaluatorPolicy`/`MetricEnforcement`/`EnforcementLevel` types
- [ ] Implement `IReportRenderer` interface in `CleanCode.Reports/` with `Format` property and `RenderAsync` method, plus `RenderOptions` record
- [ ] Implement `.cleancode.yaml` configuration schema loader in `CleanCode.Metrics/Configuration/ConfigurationLoader.cs` that reads and validates the YAML policy file

### Dependencies
- phase-foundation-and-core-models/stage-project-scaffolding-and-data-models

### Test Scenarios
- [ ] Scenario: ConfigurationLoader reads valid YAML -- Given a `.cleancode.yaml` with policy id, thresholds, and exclusion patterns, When `ConfigurationLoader.LoadAsync` is called, Then a valid `MetricConfiguration` is returned with all fields populated
- [ ] Scenario: ConfigurationLoader handles missing file -- Given no `.cleancode.yaml` exists in the repository root, When `ConfigurationLoader.LoadAsync` is called, Then it returns a default `MetricConfiguration` with conservative thresholds
- [ ] Scenario: Interface contracts compile -- Given all interfaces defined in their assemblies, When a mock implementation of each interface is instantiated, Then the DI container resolves all dependencies without error

# Phase 2: Metric Calculators

## Dependencies
- phase-foundation-and-core-models

## Stage 2.1: Coupling and Cohesion Calculators

### Implementation Steps
- [ ] Implement `CouplingCalculator` in `CleanCode.Metrics/Calculators/CouplingCalculator.cs` computing afferent coupling (Ca), efferent coupling (Ce), instability (I), abstractness (A), and distance from main sequence (D) by walking `HypergraphData` edges
- [ ] Implement adapter layer in `CleanCode.Metrics/Adapters/ArchitectureMetricsAdapter.cs` that wraps existing `ArchitectureMetricsCalculator` output into `MetricValue` instances to avoid duplicating coupling/cohesion logic
- [ ] Implement `CohesionCalculator` in `CleanCode.Metrics/Calculators/CohesionCalculator.cs` computing LCOM4 (connected components in method-field graph) and TCC (tight class cohesion ratio) per class from `HypergraphData`
- [ ] Create unit test project `src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj` with Reqnroll and MSTest references

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Afferent coupling count -- Given a HypergraphData with class A referenced by classes B, C, and D, When CouplingCalculator computes `coupling.afferent` for class A, Then the value is 3
- [ ] Scenario: Instability calculation -- Given a namespace with Ca=4 and Ce=6, When CouplingCalculator computes `coupling.instability`, Then the value is 0.6
- [ ] Scenario: LCOM4 single component -- Given a class where all methods share at least one field, When CohesionCalculator computes `cohesion.lcom4`, Then the value is 1
- [ ] Scenario: LCOM4 disconnected class -- Given a class with two method groups that share no fields, When CohesionCalculator computes `cohesion.lcom4`, Then the value is 2

## Stage 2.2: SOLID Principle Calculators

### Implementation Steps
- [ ] Implement `SrpCalculator` in `CleanCode.Metrics/Calculators/Solid/SrpCalculator.cs` computing responsibility count per class by clustering methods based on shared field access patterns
- [ ] Implement `OcpCalculator` in `CleanCode.Metrics/Calculators/Solid/OcpCalculator.cs` computing the ratio of virtual/abstract extension points to concrete modifications using `HypergraphData` and `InheritanceAnalyzer` output
- [ ] Implement `LspCalculator` in `CleanCode.Metrics/Calculators/Solid/LspCalculator.cs` detecting derived types that narrow preconditions by analyzing override patterns via `InheritanceAnalyzer`
- [ ] Implement `IspCalculator` in `CleanCode.Metrics/Calculators/Solid/IspCalculator.cs` computing average interface member count and detecting clients that use less than 50% of interface members
- [ ] Implement `DipCalculator` in `CleanCode.Metrics/Calculators/Solid/DipCalculator.cs` computing the ratio of dependencies on abstractions vs concretions per namespace using `DependencyInjectionAnalyzer` output

### Dependencies
- phase-metric-calculators/stage-coupling-and-cohesion-calculators

### Test Scenarios
- [ ] Scenario: SRP single responsibility -- Given a class with all methods accessing the same field cluster, When SrpCalculator computes `solid.srp`, Then value is 1.0
- [ ] Scenario: SRP multiple responsibilities -- Given a class with two disjoint method-field clusters, When SrpCalculator computes `solid.srp`, Then value is 2.0
- [ ] Scenario: DIP high abstraction ratio -- Given a namespace where 8 of 10 dependencies target interfaces, When DipCalculator computes `solid.dip`, Then value is 0.8 (passes threshold of 0.7)
- [ ] Scenario: ISP fat interface -- Given an interface with 15 members, When IspCalculator computes `solid.isp`, Then value is 15 and exceeds pass threshold of 7

## Stage 2.3: Ast.Core Metadata Extension and Complexity Calculators

### Implementation Steps
- [ ] Extend `HypergraphNode.Metadata` in `src/ast/Ast.Core/HypergraphModels.cs` to include method-level complexity tags (`CyclomaticComplexity`, `CognitiveComplexity`, `ParameterCount`) so downstream calculators can read pre-computed values from the hypergraph without requiring a Roslyn workspace
- [ ] Update `RepositoryProcessor` in `src/ast/Ast.Core/RepositoryProcessor.cs` to populate the new complexity metadata tags during AST extraction by analyzing method bodies with Roslyn during the parse phase
- [ ] Implement `CyclomaticComplexityCalculator` in `CleanCode.Metrics/Calculators/Complexity/CyclomaticComplexityCalculator.cs` that reads complexity from `HypergraphNode.Metadata` tags (preferred) or falls back to Roslyn syntax tree analysis when tags are absent
- [ ] Implement `CognitiveComplexityCalculator` in `CleanCode.Metrics/Calculators/Complexity/CognitiveComplexityCalculator.cs` following SonarSource cognitive complexity rules (nesting increments, structural increments), reading from metadata tags or Roslyn fallback
- [ ] Implement `WeightedMethodsCalculator` in `CleanCode.Metrics/Calculators/Complexity/WeightedMethodsCalculator.cs` summing per-method cyclomatic complexity at the class level
- [ ] Implement `LayerAdherenceCalculator` in `CleanCode.Metrics/Calculators/Architecture/LayerAdherenceCalculator.cs` wrapping `LayerViolationDetector` to compute percentage of compliant interactions
- [ ] Implement `BlastRadiusCalculator` in `CleanCode.Metrics/Calculators/Architecture/BlastRadiusCalculator.cs` using `HypergraphData` edges (and optionally `IGraphStore` Cypher queries) to count transitive dependents
- [ ] Implement `DebtRatioCalculator` in `CleanCode.Metrics/Calculators/Architecture/DebtRatioCalculator.cs` estimating remediation effort / development effort from violation count and severity

### Dependencies
- phase-metric-calculators/stage-coupling-and-cohesion-calculators

### Test Scenarios
- [ ] Scenario: Metadata tags populated during parse -- Given a C# file with a method containing 2 if-statements, When RepositoryProcessor parses it into HypergraphData, Then the method node Metadata contains key `CyclomaticComplexity` with value 3
- [ ] Scenario: Cyclomatic complexity of simple method -- Given a method with one if-statement and one for-loop, When CyclomaticComplexityCalculator computes `complexity.cyclomatic`, Then value is 3
- [ ] Scenario: Layer adherence perfect -- Given a project with 100 inter-layer interactions and zero LayerViolations, When LayerAdherenceCalculator computes `arch.layer_adherence`, Then value is 1.0
- [ ] Scenario: Blast radius calculation -- Given class A is depended upon by classes B, C where C is also depended upon by D, When BlastRadiusCalculator computes `arch.blast_radius` for A, Then value is 3 (B + C + D transitively)

# Phase 3: Orchestration and Delta

## Dependencies
- phase-metric-calculators

## Stage 3.1: Metric Orchestrator

### Implementation Steps
- [ ] Implement `MetricOrchestrator` in `CleanCode.Metrics/Orchestration/MetricOrchestrator.cs` that discovers all registered `IMetricCalculator` instances via DI, invokes each in parallel, collects results, calls `IMetricAggregator`, and assembles the final `MetricSnapshot`
- [ ] Implement `MetricAggregator` in `CleanCode.Metrics/Orchestration/MetricAggregator.cs` supporting all `AggregationStrategy` values (Mean, Median, P95, Max, Sum, WeightedByLoc) to roll up method-level metrics to namespace/project/repo scope
- [ ] Implement DI registration extension method `AddCleanCodeMetrics(this IServiceCollection)` in `CleanCode.Metrics/DependencyInjection/ServiceCollectionExtensions.cs` that registers all calculators, orchestrator, and aggregator
- [ ] Implement incremental mode in `MetricOrchestrator.RunIncrementalAsync` that accepts a list of changed files and only invokes calculators for classes/namespaces containing those files

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Orchestrator runs all calculators -- Given 3 registered IMetricCalculator mocks each returning 2 MetricValues, When MetricOrchestrator.RunAsync is called, Then the resulting MetricSnapshot contains 6 MetricValues
- [ ] Scenario: Incremental mode filters scope -- Given 50 classes in the hypergraph and 2 changed files mapping to 2 classes, When RunIncrementalAsync is called, Then only calculators scoped to those 2 classes are invoked (verified via mock call counts)
- [ ] Scenario: Aggregator mean strategy -- Given 4 MetricValues with values [2, 4, 6, 8] at Class scope, When Aggregate is called with Mean strategy and target scope Namespace, Then the resulting MetricValue has value 5.0

## Stage 3.2: Delta Calculator and Baseline Storage

### Implementation Steps
- [ ] Implement `DeltaCalculator` in `CleanCode.Metrics/Delta/DeltaCalculator.cs` that compares two `MetricSnapshot` instances by matching on MetricId+Target, producing `MetricDelta` entries with regression flags
- [ ] Implement `HasRegression` method that evaluates whether any deltas cross threshold boundaries according to `EvaluatorPolicy` enforcement levels
- [ ] Implement `BaselineStore` in `CleanCode.Metrics/Baseline/BaselineStore.cs` that reads/writes `MetricSnapshot` JSON to git notes at `refs/notes/metrics/<branch>` using `git notes` CLI commands
- [ ] Implement `BaselineStore.InitAsync` that creates the notes ref if it does not exist, and `BaselineStore.GetLatestAsync` that retrieves the most recent snapshot for a given branch

### Dependencies
- phase-orchestration-and-delta/stage-metric-orchestrator

### Test Scenarios
- [ ] Scenario: Delta detects regression -- Given a baseline snapshot with `complexity.cyclomatic` value 8 and current snapshot with value 12, When DeltaCalculator.ComputeDeltas is called, Then the resulting MetricDelta has Regressed=true (threshold PassBelow=10)
- [ ] Scenario: Delta no regression -- Given baseline and current snapshots where all metrics remain within thresholds, When DeltaCalculator.ComputeDeltas is called, Then HasRegression returns false
- [ ] Scenario: BaselineStore round-trip -- Given a MetricSnapshot serialized to git notes, When BaselineStore.GetLatestAsync is called, Then the deserialized snapshot matches the original

# Phase 4: Evaluator Agent Contract

## Dependencies
- phase-orchestration-and-delta

## Stage 4.1: Evaluator Prompt and Contract Implementation

### Implementation Steps
- [ ] Implement `EvaluatorService` in `CleanCode.Evaluator/EvaluatorService.cs` implementing `IEvaluatorContract` that serializes `MetricSnapshot` + `EvaluatorPolicy` to a structured JSON prompt payload
- [ ] Create the evaluator system prompt template in `CleanCode.Evaluator/Prompts/evaluator-system.md` defining the metric-to-verdict judgment rules, severity classification, and required JSON response schema
- [ ] Implement `LlmClient` abstraction in `CleanCode.Evaluator/LlmClient.cs` with configurable endpoint URL (from `.cleancode.yaml` or environment variable `CLEANCODE_LLM_ENDPOINT`) using `HttpClient`
- [ ] Implement JSON response parser in `CleanCode.Evaluator/ResponseParser.cs` that validates LLM response against `EvaluationVerdict` schema and handles malformed responses gracefully (retry once, then return raw-metric-based verdict)
- [ ] Implement `EvaluatorPolicy` loader that reads enforcement levels (Blocking, Advisory, Informational) per metric from `.cleancode.yaml` evaluator section

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Evaluator returns structured verdict -- Given a MetricSnapshot with 2 failing metrics and a mock LLM returning valid JSON, When EvaluatorService.EvaluateAsync is called, Then EvaluationVerdict has Outcome=Fail and 2 Violations
- [ ] Scenario: Malformed LLM response fallback -- Given a mock LLM returning invalid JSON, When EvaluatorService.EvaluateAsync is called, Then it retries once, then falls back to threshold-based verdict (no crash)
- [ ] Scenario: Advisory enforcement does not block -- Given a metric with EnforcementLevel=Advisory that fails, When HasRegression is checked against the evaluator policy, Then the overall verdict is Warn (not Fail)

# Phase 5: Reports and CLI Integration

## Dependencies
- phase-evaluator-agent-contract

## Stage 5.1: Report Renderers

### Implementation Steps
- [ ] Implement `SarifRenderer` in `CleanCode.Reports/Renderers/SarifRenderer.cs` producing SARIF 2.1.0 output with `runs[].results[]` mapping violations to file locations, using the `Sarif.Sdk` NuGet package
- [ ] Implement `DashboardExporter` in `CleanCode.Reports/Renderers/DashboardExporter.cs` producing a versioned JSON payload (schema version from config) with metric summaries, trend-friendly fields, and category breakdowns
- [ ] Implement `MarkdownRenderer` in `CleanCode.Reports/Renderers/MarkdownRenderer.cs` producing a human-readable summary table of pass/warn/fail counts, top violations, and hotspot list
- [ ] Add unit tests validating SARIF output against the SARIF 2.1.0 JSON schema

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: SARIF schema compliance -- Given a MetricSnapshot with 3 violations, When SarifRenderer.RenderAsync is called, Then the output validates against the SARIF 2.1.0 JSON schema with correct result locations
- [ ] Scenario: Dashboard JSON versioning -- Given config with `dashboardSchemaVersion: "1.0"`, When DashboardExporter.RenderAsync is called, Then the JSON payload contains `"schemaVersion": "1.0"` at the root
- [ ] Scenario: Markdown includes hotspots -- Given a MetricSnapshot with hotspot entries, When MarkdownRenderer.RenderAsync is called, Then output contains a "Top Hotspots" section with ranked entries

## Stage 5.2: CLI Commands

### Implementation Steps
- [ ] Implement `MetricsCommand` in `src/ast/Ast.Cli/Commands/MetricsCommand.cs` as a Spectre.Console command with options: `--repo`, `--output`, `--format` (sarif|json|markdown), `--diff` (incremental mode), `--hotspots`, `--top N`
- [ ] Implement `EvaluateCommand` in `src/ast/Ast.Cli/Commands/EvaluateCommand.cs` with options: `--snapshot`, `--policy`, `--output` that invokes `IEvaluatorContract` and exits with code 0/1/2
- [ ] Implement `BaselineCommand` in `src/ast/Ast.Cli/Commands/BaselineCommand.cs` with subcommands `--init` (create notes ref) and `--update` (store current snapshot as new baseline)
- [ ] Register all three commands in `Program.cs` and wire DI using `AddCleanCodeMetrics` extension
- [ ] Implement exit code logic: 0 for pass, 1 for fail, 2 for warn-only per tech-spec constraint C8

### Dependencies
- phase-reports-and-cli-integration/stage-report-renderers

### Test Scenarios
- [ ] Scenario: Metrics command full scan -- Given a valid repository path, When `ast-cli metrics --repo . --output report.sarif` is invoked, Then exit code is 0 and `report.sarif` file is written to disk
- [ ] Scenario: Evaluate command fail exit -- Given a snapshot file where composite score is below fail threshold, When `ast-cli evaluate --snapshot snap.json` is invoked, Then exit code is 1
- [ ] Scenario: Baseline init creates notes ref -- Given a git repository with no metrics notes, When `ast-cli baseline --init` is invoked, Then `refs/notes/metrics/main` exists in git

# Phase 6: Cross-Repo and Hotspot Ranking

## Dependencies
- phase-reports-and-cli-integration

## Stage 6.1: Cross-Repo Query Service

### Implementation Steps
- [ ] Implement `ICrossRepoQueryService` in `src/ast/Ast.GraphDb/CrossRepo/ICrossRepoQueryService.cs` with methods `GetDependencyDepthAsync`, `GetBlastRadiusAsync`, `FindCrossRepoDuplicatesAsync`, and `GetKnowledgeDistributionAsync`
- [ ] Implement `Neo4jCrossRepoQueryService` in `src/ast/Ast.GraphDb/CrossRepo/Neo4jCrossRepoQueryService.cs` with Cypher queries for dependency depth (longest path traversal) and blast radius (transitive dependent count)
- [ ] Implement `KnowledgeDistributionCalculator` in `CleanCode.Metrics/Calculators/CrossRepo/KnowledgeDistributionCalculator.cs` that aggregates `git blame` output per critical component to count unique maintainers, producing `crossrepo.knowledge_dist` metric values
- [ ] Implement duplication detection via Qdrant embedding similarity in `CrossRepoDuplicationDetector.cs` with configurable similarity threshold (default 0.9)
- [ ] Implement `MetricSnapshotPersistence` in `src/ast/Ast.GraphDb/CrossRepo/MetricSnapshotPersistence.cs` that stores `MetricSnapshot` as Neo4j graph nodes (`(:MetricSnapshot)-[:MEASURED]->(:Repository)`) for historical trending and cross-repo queries
- [ ] Implement graceful degradation: when Neo4j or Qdrant is unavailable, log warning and skip cross-repo metrics and snapshot persistence without failing the overall scan

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Dependency depth computed -- Given 3 repositories A->B->C indexed in Neo4j, When GetDependencyDepthAsync is called for repo A, Then depth is 2
- [ ] Scenario: Graceful degradation without Neo4j -- Given Neo4j is unavailable, When cross-repo calculators execute, Then they return empty results with a warning logged (no exception thrown)
- [ ] Scenario: Duplication detection -- Given two repos with a code block at cosine similarity 0.95, When FindCrossRepoDuplicatesAsync is called with threshold 0.9, Then one DuplicationMatch is returned
- [ ] Scenario: Knowledge distribution metric -- Given 3 critical components with git blame showing 5 unique maintainers total, When GetKnowledgeDistributionAsync is called, Then `crossrepo.knowledge_dist` value is 5
- [ ] Scenario: Snapshot persisted to Neo4j -- Given a valid MetricSnapshot, When MetricSnapshotPersistence.StoreAsync is called, Then a `:MetricSnapshot` node exists in Neo4j with a `:MEASURED` relationship to the repository node

## Stage 6.2: Hotspot Ranker and System Snapshot

### Implementation Steps
- [ ] Implement `HotspotRanker` in `CleanCode.Metrics/Hotspots/HotspotRanker.cs` that scores each class by weighted composite of violation severity (0.4), change frequency from `git log` (0.2), blast radius (0.2), and coupling*complexity product (0.2)
- [ ] Implement `ChangeFrequencyProvider` in `CleanCode.Metrics/Hotspots/ChangeFrequencyProvider.cs` that runs `git log --stat` to count commits touching each file in the last 90 days
- [ ] Implement `SystemMetricOrchestrator` in `CleanCode.Metrics/Orchestration/SystemMetricOrchestrator.cs` that aggregates multiple per-repo `MetricSnapshot` instances into a `SystemMetricSnapshot` with cross-repo metrics from `ICrossRepoQueryService`
- [ ] Add `--hotspots --top N` output path to `MetricsCommand` that invokes `HotspotRanker` and renders the ranked list

### Dependencies
- phase-cross-repo-and-hotspot-ranking/stage-cross-repo-query-service

### Test Scenarios
- [ ] Scenario: Hotspot ranking order -- Given class A with 5 violations and 20 commits and class B with 1 violation and 2 commits, When HotspotRanker.RankAsync is called, Then class A ranks higher than class B
- [ ] Scenario: System snapshot assembly -- Given MetricSnapshots from 3 repos, When SystemMetricOrchestrator aggregates them, Then SystemMetricSnapshot.RepositorySnapshots has count 3 and SystemMetrics contains cross-repo depth metric
- [ ] Scenario: Change frequency extraction -- Given a git repo with file X modified in 5 commits in the last 90 days, When ChangeFrequencyProvider queries, Then it returns 5 for file X

# Phase 7: Integration Testing and Calibration

## Dependencies
- phase-cross-repo-and-hotspot-ranking

## Stage 7.1: End-to-End Integration Tests

### Implementation Steps
- [ ] Create `src/ast/CleanCode.IntegrationTests/CleanCode.IntegrationTests.csproj` with BDD features for full pipeline tests
- [ ] Implement feature: full scan of `workflow` repo -- parse, compute all metrics, produce SARIF output, verify all 20+ metrics are populated in the snapshot
- [ ] Implement feature: incremental scan with synthetic bad commit -- add a known-bad class, run diff mode, verify DeltaCalculator flags the regression
- [ ] Implement feature: evaluator round-trip with mock LLM endpoint -- invoke EvaluateCommand, verify structured EvaluationVerdict returned
- [ ] Implement feature: cross-repo scan with test Neo4j data -- load 3-repo graph, run system metrics, verify dependency depth and duplication metrics

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Full pipeline produces valid snapshot -- Given the workflow repository, When `ast-cli metrics --repo .` runs end-to-end, Then MetricSnapshot contains at least 20 distinct MetricIds across all 6 categories
- [ ] Scenario: Incremental mode detects regression -- Given a baseline snapshot and a synthetic commit adding a class with cyclomatic complexity 25, When `ast-cli metrics --diff HEAD~1` runs, Then SARIF output contains a violation for the new class
- [ ] Scenario: CI exit code integration -- Given a `.cleancode.yaml` with blocking enforcement on `complexity.cyclomatic` threshold 10, When a snapshot has methods exceeding 10, Then `ast-cli evaluate` exits with code 1

## Stage 7.2: Calibration and Documentation

### Implementation Steps
- [ ] Implement `ast-cli metrics --calibrate` subcommand that analyzes the current repo state and suggests threshold values based on P50/P75/P90 of measured metrics
- [ ] Create documentation in `docs/clean-code-metrics-guide.md` covering: metric catalog with descriptions, configuration guide for `.cleancode.yaml`, CI integration examples (GitHub Actions, Azure Pipelines), and threshold tuning guidance
- [ ] Create a sample `.cleancode.yaml` at `docs/examples/.cleancode.yaml` with conservative defaults (warn-not-fail) for all metrics

### Dependencies
- phase-integration-testing-and-calibration/stage-end-to-end-integration-tests

### Test Scenarios
- [ ] Scenario: Calibrate suggests thresholds -- Given a repository with varied complexity, When `ast-cli metrics --calibrate` runs, Then output contains suggested PassBelow values for each metric at P75 of current measurements
- [ ] Scenario: Documentation accuracy -- Given the metrics guide references metric IDs, When cross-referenced with the MetricCatalog enum in source, Then all documented metric IDs exist in code and vice versa
