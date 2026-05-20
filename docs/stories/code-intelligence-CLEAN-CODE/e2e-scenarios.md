# Clean Code Metrics -- End-to-End Scenarios

> **Story**: code-intelligence:CLEAN-CODE
> **Points**: 13
> **Status**: Design
> **Sibling docs**: [architecture.md](./architecture.md) | [tech-spec.md](./tech-spec.md) | [implementation-plan.md](./implementation-plan.md)

---

# Phase 1: Foundation and Core Models

### Setup
- **Type**: inline
- **Local**: `dotnet test src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj`
- **CI runner**: `ubuntu-latest` (hosted) or `windows-latest` for win-x64 builds
- **Secrets**: none
- **Pre-test bootstrap**: `dotnet restore Workflow.sln`

### Scenarios

```gherkin
Feature: Project scaffolding and solution integration

  Scenario: New projects compile within the solution
    Given the projects CleanCode.Metrics, CleanCode.Evaluator, and CleanCode.Reports exist under src/ast/
    And each targets net8.0 with win-x64 RuntimeIdentifier
    And they are added to Workflow.sln under the ast solution folder
    When I run `dotnet build Workflow.sln`
    Then the build succeeds with zero errors
    And assemblies CleanCode.Metrics.dll, CleanCode.Evaluator.dll, and CleanCode.Reports.dll are produced

  Scenario: Project references resolve correctly
    Given CleanCode.Metrics references Ast.Core and CodeIntelligence
    And CleanCode.Evaluator references CleanCode.Metrics
    And CleanCode.Reports references CleanCode.Metrics and CleanCode.Evaluator
    When I run `dotnet build Workflow.sln`
    Then no unresolved project reference warnings are emitted

  Scenario: File header compliance
    Given all .cs files in the three new projects
    When scanned for the required copyright header
    Then every file begins with the standard Microsoft Corp. copyright block
```

```gherkin
Feature: Core data model serialization

  Scenario: MetricSnapshot round-trips through JSON
    Given a MetricSnapshot with Id, Timestamp, RepositoryId, CommitSha, 5 MetricValues, and a ScoreSummary
    When serialized to JSON via System.Text.Json
    And deserialized back to MetricSnapshot
    Then all fields match the original including nested Violations and MetricThreshold values

  Scenario: MetricThreshold evaluates warn boundary
    Given a MetricValue with Value=12
    And a MetricThreshold with PassBelow=10 and WarnBelow=15
    When the ScoreSummary evaluates the MetricValue
    Then the Outcome for that metric is Warn

  Scenario: MetricThreshold evaluates pass boundary
    Given a MetricValue with Value=5
    And a MetricThreshold with PassBelow=10
    When the ScoreSummary evaluates the MetricValue
    Then the Outcome for that metric is Pass

  Scenario: MetricThreshold evaluates fail boundary
    Given a MetricValue with Value=20
    And a MetricThreshold with PassBelow=10 and WarnBelow=15
    When the ScoreSummary evaluates the MetricValue
    Then the Outcome for that metric is Fail

  Scenario: SystemMetricSnapshot aggregates multiple repos
    Given 3 MetricSnapshots each with different RepositoryId values
    When assembled into a SystemMetricSnapshot
    Then RepositorySnapshots.Count is 3
    And Summary.CompositeScore is the weighted mean of individual scores
```

```gherkin
Feature: Configuration schema loading

  Scenario: Valid .cleancode.yaml loads correctly
    Given a .cleancode.yaml with policyId "default", thresholds for 5 metrics, and exclusion pattern "**/*Generated*"
    When ConfigurationLoader.LoadAsync is called
    Then a MetricConfiguration is returned with all thresholds populated
    And the exclusion pattern "**/*Generated*" is present in Exclusions

  Scenario: Missing .cleancode.yaml returns defaults
    Given no .cleancode.yaml exists at the repository root
    When ConfigurationLoader.LoadAsync is called
    Then a default MetricConfiguration is returned
    And all thresholds use conservative warn-not-fail defaults

  Scenario: Malformed YAML throws descriptive error
    Given a .cleancode.yaml with invalid indentation
    When ConfigurationLoader.LoadAsync is called
    Then a ConfigurationException is thrown with line number and column details

  Scenario: Exclusion patterns filter files
    Given a .cleancode.yaml with exclusion "**/*.Designer.cs"
    And a repository containing files Foo.cs and Foo.Designer.cs
    When the configuration is applied to filter analysis targets
    Then Foo.cs is included and Foo.Designer.cs is excluded
```

---

# Phase 2: Metric Calculators

### Setup
- **Type**: inline
- **Local**: `dotnet test src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj --filter "Category=calculators"`
- **CI runner**: `windows-latest` (Roslyn workspace requires Windows SDK)
- **Secrets**: none
- **Pre-test bootstrap**: `dotnet restore Workflow.sln`

### Scenarios

```gherkin
Feature: Coupling and cohesion calculators

  Scenario: Afferent coupling count
    Given a HypergraphData where class "MyApp.Services.OrderService" is referenced by 4 other classes
    When CouplingCalculator computes metric "coupling.afferent" for "MyApp.Services.OrderService"
    Then the MetricValue.Value is 4

  Scenario: Efferent coupling count
    Given a HypergraphData where class "MyApp.Services.OrderService" depends on 3 external classes
    When CouplingCalculator computes metric "coupling.efferent" for "MyApp.Services.OrderService"
    Then the MetricValue.Value is 3

  Scenario: Instability formula
    Given a namespace with afferent coupling Ca=4 and efferent coupling Ce=6
    When CouplingCalculator computes metric "coupling.instability"
    Then the value is 0.6 (Ce / (Ca + Ce))

  Scenario: Distance from main sequence
    Given a namespace with Instability=0.6 and Abstractness=0.3
    When CouplingCalculator computes metric "coupling.distance"
    Then the value is 0.1 (|A + I - 1|)

  Scenario: LCOM4 fully cohesive class
    Given a class where all methods share at least one common field
    When CohesionCalculator computes metric "cohesion.lcom4"
    Then the value is 1

  Scenario: LCOM4 disconnected class
    Given a class with two groups of methods that share no fields between groups
    When CohesionCalculator computes metric "cohesion.lcom4"
    Then the value is 2

  Scenario: TCC tight class cohesion
    Given a class with 4 methods where 4 of 6 method-pairs share field access
    When CohesionCalculator computes metric "cohesion.tcc"
    Then the value is approximately 0.67
```

```gherkin
Feature: SOLID principle calculators

  Scenario: SRP single responsibility detected
    Given a class where all methods form one access-cluster (share field dependencies)
    When SrpCalculator computes metric "solid.srp"
    Then the value is 1.0

  Scenario: SRP multiple responsibilities detected
    Given a class with two disjoint method-field clusters
    When SrpCalculator computes metric "solid.srp"
    Then the value is 2.0

  Scenario: SRP threshold evaluation
    Given a class with solid.srp value of 3.0
    And threshold PassBelow=1.5
    When evaluated against the threshold
    Then the outcome is Fail

  Scenario: OCP extension point ratio
    Given a class with 8 virtual/abstract members and 2 sealed modifications
    When OcpCalculator computes metric "solid.ocp"
    Then the value is 0.8

  Scenario: LSP violation detection
    Given a derived class that narrows a base method precondition (throws on valid base inputs)
    When LspCalculator computes metric "solid.lsp"
    Then at least one Violation is produced with severity High

  Scenario: ISP fat interface detection
    Given an interface IRepository with 15 members
    And a class implementing IRepository that uses only 5 of those members
    When IspCalculator computes metric "solid.isp"
    Then the value is 15 (member count)
    And the metric exceeds the default PassBelow threshold of 7

  Scenario: DIP abstraction dependency ratio
    Given a namespace where 8 of 10 type dependencies target interfaces or abstract classes
    When DipCalculator computes metric "solid.dip"
    Then the value is 0.8
    And the metric passes the default threshold of 0.7
```

```gherkin
Feature: Complexity calculators

  Scenario: Cyclomatic complexity simple method
    Given a method with 1 if-statement and 1 for-loop
    When CyclomaticComplexityCalculator computes metric "complexity.cyclomatic"
    Then the value is 3 (1 base + 1 if + 1 for)

  Scenario: Cyclomatic complexity reads from metadata tags
    Given a HypergraphNode with Metadata tag "CyclomaticComplexity" = "5"
    When CyclomaticComplexityCalculator processes that node
    Then it uses the tag value 5 without invoking Roslyn workspace

  Scenario: Cognitive complexity nested structures
    Given a method with a for-loop containing an if-statement containing a try-catch
    When CognitiveComplexityCalculator computes metric "complexity.cognitive"
    Then the value accounts for nesting increments (structural + nesting penalty)

  Scenario: Weighted methods per class
    Given a class with 3 methods having cyclomatic complexities 2, 5, and 8
    When WeightedMethodsCalculator computes metric "complexity.wmc"
    Then the value is 15

  Scenario: Metadata tag population during parse
    Given a C# file with a method containing 3 if-statements
    When CSharpHyperedgeExtractor processes the file
    Then the method node Metadata contains key "CyclomaticComplexity" with value "4"
```

```gherkin
Feature: Architecture calculators

  Scenario: Layer adherence perfect compliance
    Given a project with 100 inter-layer interactions and 0 violations from LayerViolationDetector
    When LayerAdherenceCalculator computes metric "arch.layer_adherence"
    Then the value is 1.0

  Scenario: Layer adherence with violations
    Given 100 interactions and 10 LayerViolations
    When LayerAdherenceCalculator computes metric "arch.layer_adherence"
    Then the value is 0.9

  Scenario: Blast radius transitive dependents
    Given class A depends on nothing, class B depends on A, class C depends on B, class D depends on C
    When BlastRadiusCalculator computes metric "arch.blast_radius" for class A
    Then the value is 3 (B, C, D transitively depend on A)

  Scenario: Debt ratio estimation
    Given 5 violations with severities [Critical, High, Medium, Medium, Low]
    When DebtRatioCalculator computes metric "arch.debt_ratio"
    Then the value reflects weighted violation cost / total development cost estimate
```

---

# Phase 3: Orchestration and Delta Calculation

### Setup
- **Type**: inline
- **Local**: `dotnet test src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj --filter "Category=orchestration"`
- **CI runner**: `windows-latest`
- **Secrets**: none
- **Pre-test bootstrap**: `dotnet restore Workflow.sln`

### Scenarios

```gherkin
Feature: Metric orchestrator

  Scenario: Orchestrator runs all registered calculators
    Given 5 registered IMetricCalculator implementations via DI
    And each returns 3 MetricValues
    When MetricOrchestrator.RunAsync is called with a HypergraphData
    Then the resulting MetricSnapshot contains 15 MetricValues
    And the ScoreSummary CompositeScore is computed from all values

  Scenario: Orchestrator runs calculators in parallel
    Given 3 calculators that each take 1 second
    When MetricOrchestrator.RunAsync is called
    Then total execution time is less than 2 seconds (parallel, not 3s serial)

  Scenario: Incremental mode scopes to changed files
    Given a HypergraphData with 50 classes
    And 2 changed files mapping to classes "Foo" and "Bar"
    When MetricOrchestrator.RunIncrementalAsync is called with those 2 files
    Then only MetricValues targeting "Foo" or "Bar" classes are produced

  Scenario: Aggregator mean strategy
    Given 4 MetricValues at Class scope with values [2, 4, 6, 8]
    When MetricAggregator aggregates with strategy Mean and target scope Namespace
    Then the resulting MetricValue has value 5.0

  Scenario: Aggregator P95 strategy
    Given 100 MetricValues at Method scope with uniformly distributed values 1-100
    When MetricAggregator aggregates with strategy P95 and target scope Class
    Then the resulting MetricValue has value approximately 95

  Scenario: Aggregator weighted-by-LOC strategy
    Given 2 MetricValues: class A (100 LOC, complexity 5) and class B (400 LOC, complexity 10)
    When MetricAggregator aggregates with strategy WeightedByLoc at Project scope
    Then the resulting value is 9.0 ((100*5 + 400*10) / 500)
```

```gherkin
Feature: Delta calculator and baseline storage

  Scenario: Delta detects regression
    Given a baseline MetricSnapshot with "complexity.cyclomatic" value 8 for class X
    And a current MetricSnapshot with "complexity.cyclomatic" value 12 for class X
    And threshold PassBelow=10
    When DeltaCalculator.ComputeDeltas is called
    Then the MetricDelta for "complexity.cyclomatic" has Regressed=true
    And Delta=4

  Scenario: Delta detects improvement
    Given a baseline with "coupling.afferent" value 15 and current value 8
    And threshold PassBelow=10
    When DeltaCalculator.ComputeDeltas is called
    Then the MetricDelta has Regressed=false
    And Delta=-7

  Scenario: Delta with no baseline metric (new class)
    Given a baseline MetricSnapshot with no entry for class "NewClass"
    And a current MetricSnapshot with "complexity.cyclomatic" value 5 for "NewClass"
    When DeltaCalculator.ComputeDeltas is called
    Then the MetricDelta has BaselineValue=0 and Regressed=false (within threshold)

  Scenario: HasRegression respects enforcement levels
    Given a delta where "solid.isp" regressed from 6 to 9 (threshold PassBelow=7)
    And the EvaluatorPolicy sets "solid.isp" enforcement to Advisory
    When HasRegression is checked
    Then the result is false (Advisory does not block)

  Scenario: BaselineStore round-trip via git notes
    Given a MetricSnapshot with 10 MetricValues
    When BaselineStore stores it at refs/notes/metrics/main
    And BaselineStore.GetLatestAsync retrieves from refs/notes/metrics/main
    Then the retrieved snapshot matches the original exactly

  Scenario: BaselineStore init creates notes ref
    Given a git repository with no refs/notes/metrics namespace
    When BaselineStore.InitAsync is called for branch "main"
    Then refs/notes/metrics/main exists in the git refs
```

---

# Phase 4: Evaluator Agent Contract

### Setup
- **Type**: inline
- **Local**: `dotnet test src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj --filter "Category=evaluator"`
- **CI runner**: `windows-latest`
- **Secrets**: none (uses mock LLM endpoint in tests)
- **Pre-test bootstrap**: `dotnet restore Workflow.sln`

### Scenarios

```gherkin
Feature: Evaluator service with LLM contract

  Scenario: Evaluator returns structured verdict for failing metrics
    Given a MetricSnapshot with 2 metrics exceeding Fail thresholds
    And a mock LLM endpoint returning valid EvaluationVerdict JSON
    When EvaluatorService.EvaluateAsync is called
    Then EvaluationVerdict.Outcome is Fail
    And Violations.Count is 2
    And each Violation has RuleId, Severity, Target, and Message populated

  Scenario: Evaluator returns pass for clean snapshot
    Given a MetricSnapshot where all metrics are within Pass thresholds
    And a mock LLM endpoint returning a Pass verdict
    When EvaluatorService.EvaluateAsync is called
    Then EvaluationVerdict.Outcome is Pass
    And Violations is empty
    And OverallScore is greater than 0.8

  Scenario: Malformed LLM response triggers retry then fallback
    Given a mock LLM endpoint returning malformed text on first call
    And valid JSON on retry (second call)
    When EvaluatorService.EvaluateAsync is called
    Then the service retries once
    And returns the valid EvaluationVerdict from the second attempt

  Scenario: LLM endpoint permanently unavailable triggers threshold fallback
    Given a mock LLM endpoint that always returns HTTP 500
    When EvaluatorService.EvaluateAsync is called
    Then it retries once
    Then it falls back to pure threshold-based verdict computation
    And no exception propagates to the caller

  Scenario: Advisory enforcement produces Warn not Fail
    Given a MetricSnapshot with "solid.isp" value 12 (threshold PassBelow=7)
    And EvaluatorPolicy sets "solid.isp" enforcement to Advisory
    When the overall verdict is computed
    Then VerdictOutcome is Warn (not Fail)

  Scenario: Blocking enforcement produces Fail
    Given a MetricSnapshot with "complexity.cyclomatic" value 25 (threshold PassBelow=10)
    And EvaluatorPolicy sets "complexity.cyclomatic" enforcement to Blocking
    When the overall verdict is computed
    Then VerdictOutcome is Fail

  Scenario: Informational enforcement does not affect verdict
    Given a MetricSnapshot with "coupling.distance" value 0.8 (threshold PassBelow=0.5)
    And EvaluatorPolicy sets "coupling.distance" enforcement to Informational
    When the overall verdict is computed
    Then VerdictOutcome is Pass
    And the violation is logged but does not influence Outcome

  Scenario: Evaluator prompt payload schema correctness
    Given a MetricSnapshot and EvaluatorPolicy
    When EvaluatorService serializes the prompt payload
    Then the JSON payload contains keys "snapshot", "policy", and "instructions"
    And "snapshot.metrics" is an array of metric objects with id, value, threshold fields

  Scenario: LLM endpoint is configurable via environment variable
    Given env var CLEANCODE_LLM_ENDPOINT is set to "http://localhost:11434/v1/chat"
    When EvaluatorService initializes
    Then it uses that endpoint URL for LLM calls
    And does not read the .cleancode.yaml endpoint field

  Scenario: Evaluator completes within performance budget
    Given a MetricSnapshot with 25 metrics
    And a mock LLM endpoint with 2-second response latency
    When EvaluatorService.EvaluateAsync is called
    Then it completes in less than 60 seconds total
```

---

# Phase 5: Reports and CLI Integration

### Setup
- **Type**: inline
- **Local**: `dotnet test src/ast/CleanCode.Metrics.UnitTests/CleanCode.Metrics.UnitTests.csproj --filter "Category=reports|Category=cli"` and `dotnet build src/ast/Ast.Cli/Ast.Cli.csproj`
- **CI runner**: `windows-latest`
- **Secrets**: none
- **Pre-test bootstrap**: `dotnet restore Workflow.sln`

### Scenarios

```gherkin
Feature: SARIF report renderer

  Scenario: SARIF 2.1.0 schema compliance
    Given a MetricSnapshot with 3 Violations with file locations
    When SarifRenderer.RenderAsync is called
    Then output is valid SARIF 2.1.0 JSON
    And runs[0].results has 3 entries
    And each result has ruleId, message, and locations[0].physicalLocation

  Scenario: SARIF maps violations to correct file paths
    Given a Violation targeting "MyApp.Services.OrderService" at file "src/Services/OrderService.cs" line 42
    When SarifRenderer.RenderAsync is called
    Then the SARIF result physicalLocation.artifactLocation.uri is "src/Services/OrderService.cs"
    And region.startLine is 42

  Scenario: SARIF with no violations produces empty results array
    Given a MetricSnapshot with all metrics passing
    When SarifRenderer.RenderAsync is called
    Then runs[0].results is an empty array
    And the output is still valid SARIF 2.1.0
```

```gherkin
Feature: Dashboard JSON exporter

  Scenario: Dashboard payload includes schema version
    Given MetricConfiguration with dashboardSchemaVersion "1.0"
    When DashboardExporter.RenderAsync is called
    Then the JSON root contains "schemaVersion": "1.0"

  Scenario: Dashboard payload category breakdown
    Given a MetricSnapshot with metrics across categories Coupling, SOLID, and Complexity
    When DashboardExporter.RenderAsync is called
    Then the JSON contains a "categories" array with entries for each category
    And each entry has "name", "score", and "metricCount" fields

  Scenario: Dashboard payload trend fields
    Given a MetricSnapshot with CommitSha and Timestamp
    When DashboardExporter.RenderAsync is called
    Then the JSON contains "commitSha" and "measuredAt" fields suitable for time-series plotting
```

```gherkin
Feature: Markdown report renderer

  Scenario: Markdown summary table
    Given a MetricSnapshot with PassCount=15, WarnCount=3, FailCount=2
    When MarkdownRenderer.RenderAsync is called
    Then output contains a summary table with Pass/Warn/Fail columns
    And values 15, 3, 2 appear in the table

  Scenario: Markdown includes hotspot section
    Given a MetricSnapshot with 5 HotspotEntry items
    When MarkdownRenderer.RenderAsync is called
    Then output contains a "Top Hotspots" section
    And lists all 5 entries ranked by CompositeScore descending
```

```gherkin
Feature: CLI metrics command

  Scenario: Full scan produces SARIF output
    Given a valid repository at the current path
    When `ast-cli metrics --repo . --output report.sarif --format sarif` is invoked
    Then exit code is 0
    And file report.sarif exists on disk
    And report.sarif is valid SARIF 2.1.0 JSON

  Scenario: Full scan produces JSON dashboard output
    Given a valid repository at the current path
    When `ast-cli metrics --repo . --output dashboard.json --format json` is invoked
    Then exit code is 0
    And file dashboard.json exists with schemaVersion field

  Scenario: Full scan produces Markdown output
    Given a valid repository at the current path
    When `ast-cli metrics --repo . --output report.md --format markdown` is invoked
    Then exit code is 0
    And file report.md contains a summary table

  Scenario: Incremental diff mode
    Given a repository with a baseline stored in git notes
    And 3 files modified since the baseline commit
    When `ast-cli metrics --repo . --diff HEAD~1 --output delta.sarif` is invoked
    Then only metrics for the 3 changed files are computed
    And delta information is included in the SARIF output

  Scenario: Hotspot command output
    Given a repository with varied metric violations
    When `ast-cli metrics --repo . --hotspots --top 10` is invoked
    Then output lists up to 10 hotspot entries
    And entries are sorted by composite score descending
    And each entry shows target, violation count, and suggested refactoring

  Scenario: Full scan performance budget
    Given a C# solution with approximately 500 files
    When `ast-cli metrics --repo .` is invoked
    Then the command completes in less than 5 minutes
```

```gherkin
Feature: CLI evaluate command

  Scenario: Evaluate command pass exit code
    Given a snapshot file where all metrics pass thresholds
    When `ast-cli evaluate --snapshot pass.json` is invoked
    Then exit code is 0

  Scenario: Evaluate command fail exit code
    Given a snapshot file with metrics exceeding Blocking fail thresholds
    When `ast-cli evaluate --snapshot fail.json` is invoked
    Then exit code is 1

  Scenario: Evaluate command warn-only exit code
    Given a snapshot file with metrics exceeding Advisory thresholds only
    When `ast-cli evaluate --snapshot warn.json` is invoked
    Then exit code is 2

  Scenario: Evaluate with custom policy override
    Given a snapshot file and a custom policy YAML at custom-policy.yaml
    When `ast-cli evaluate --snapshot snap.json --policy custom-policy.yaml` is invoked
    Then the custom policy thresholds are used instead of defaults
```

```gherkin
Feature: CLI baseline command

  Scenario: Baseline init creates notes ref
    Given a git repository with no refs/notes/metrics/main
    When `ast-cli baseline --init` is invoked
    Then refs/notes/metrics/main exists in git
    And exit code is 0

  Scenario: Baseline update stores current snapshot
    Given a repository with an existing metrics notes ref
    And a current MetricSnapshot has been computed
    When `ast-cli baseline --update` is invoked
    Then the snapshot JSON is stored in refs/notes/metrics/main
    And git notes show returns the serialized snapshot

  Scenario: Baseline init is idempotent
    Given refs/notes/metrics/main already exists
    When `ast-cli baseline --init` is invoked again
    Then exit code is 0
    And the existing baseline is not overwritten
```

---

# Phase 6: Cross-Repo Metrics and Hotspot Ranking

### Setup
- **Type**: compose
- **Local**: `docker-compose -f tests/e2e/cross-repo/docker-compose.yml up -d && dotnet test src/ast/CleanCode.IntegrationTests/CleanCode.IntegrationTests.csproj --filter "Category=crossrepo"`
- **CI runner**: `windows-latest` with Docker support (self-hosted pool `forge-build-docker`)
- **Secrets**: none (Neo4j and Qdrant run locally via compose)
- **Pre-test bootstrap**: `docker-compose -f tests/e2e/cross-repo/docker-compose.yml up -d --wait`

The compose file at `tests/e2e/cross-repo/docker-compose.yml` starts:
- `neo4j` -- Neo4j 5.26-community on ports 7474/7687 with APOC plugin
- `qdrant` -- Qdrant latest on ports 6333/6334

Environment variables:
- `NEO4J_URI=bolt://localhost:7687`
- `NEO4J_USER=neo4j`
- `NEO4J_PASSWORD` (from compose env, default test value)
- `QDRANT_URI=http://localhost:6333`

### Scenarios

```gherkin
Feature: Cross-repo query service

  Scenario: Dependency depth across repositories
    Given 3 repositories A, B, C indexed in Neo4j where A depends on B and B depends on C
    When ICrossRepoQueryService.GetDependencyDepthAsync is called for repo A
    Then the depth value is 2

  Scenario: Cross-repo blast radius
    Given repo A exports a package consumed by repos B, C, D
    When ICrossRepoQueryService.GetBlastRadiusAsync is called for the exported package
    Then the blast radius is 3

  Scenario: Cross-repo duplication detection
    Given two repos with a code block at cosine similarity 0.95
    And the duplication threshold is configured at 0.9
    When ICrossRepoQueryService.FindCrossRepoDuplicatesAsync is called
    Then one DuplicationMatch is returned with similarity 0.95

  Scenario: Knowledge distribution metric
    Given git blame for 3 critical components shows 5 unique maintainers
    When GetKnowledgeDistributionAsync is called
    Then crossrepo.knowledge_dist metric value is 5

  Scenario: Graceful degradation when Neo4j is unavailable
    Given Neo4j connection is refused (container stopped)
    When cross-repo calculators execute
    Then they return empty MetricValue results
    And a warning is logged with message containing "Neo4j unavailable"
    And no exception propagates

  Scenario: Graceful degradation when Qdrant is unavailable
    Given Qdrant connection is refused (container stopped)
    When duplication detection executes
    Then it returns empty results
    And a warning is logged
    And the overall scan completes successfully

  Scenario: MetricSnapshot persisted to Neo4j
    Given a valid MetricSnapshot for repo "workflow"
    When MetricSnapshotPersistence.StoreAsync is called
    Then a :MetricSnapshot node exists in Neo4j
    And it has a :MEASURED relationship to the :Repository node for "workflow"
    And the node properties include timestamp and composite score
```

```gherkin
Feature: Hotspot ranking

  Scenario: Hotspot ranking order by composite score
    Given class A with 5 violations, 20 commits in 90 days, blast radius 8
    And class B with 1 violation, 2 commits in 90 days, blast radius 2
    When HotspotRanker.RankAsync is called
    Then class A ranks higher (worse) than class B

  Scenario: Hotspot ranking weighted formula
    Given the weights are severity=0.4, changeFreq=0.2, blastRadius=0.2, couplingComplexity=0.2
    And class X has normalized scores severity=0.8, changeFreq=0.5, blastRadius=0.6, couplingComplexity=0.4
    When HotspotRanker.RankAsync computes the composite
    Then the score is (0.4*0.8 + 0.2*0.5 + 0.2*0.6 + 0.2*0.4) = 0.62

  Scenario: Hotspot top-N limiting
    Given 50 classes with varying violations
    When HotspotRanker.RankAsync is called with top=10
    Then exactly 10 HotspotEntry items are returned
    And they are the 10 highest composite scores

  Scenario: Change frequency extraction
    Given a git repo where file X was modified in 7 commits in the last 90 days
    When ChangeFrequencyProvider queries for file X
    Then it returns ChangeFrequency=7

  Scenario: Suggested refactoring patterns
    Given a class with solid.srp value 4.0 (4 responsibilities)
    When HotspotRanker produces the HotspotEntry
    Then SuggestedRefactoring contains "Split Class" or "Extract Class"

  Scenario: System snapshot assembly
    Given MetricSnapshots from repos A, B, and C
    And cross-repo metrics computed via ICrossRepoQueryService
    When SystemMetricOrchestrator aggregates them
    Then SystemMetricSnapshot.RepositorySnapshots.Count is 3
    And SystemMetrics contains crossrepo.dependency_depth and crossrepo.knowledge_dist
```

---

# Phase 7: Full Pipeline Integration and Calibration

### Setup
- **Type**: compose
- **Local**: `docker-compose -f tests/e2e/full-pipeline/docker-compose.yml up -d && dotnet test src/ast/CleanCode.IntegrationTests/CleanCode.IntegrationTests.csproj`
- **CI runner**: `windows-latest` with Docker support (self-hosted pool `forge-build-docker`)
- **Secrets**: none (all services local; mock LLM via WireMock container)
- **Pre-test bootstrap**: `docker-compose -f tests/e2e/full-pipeline/docker-compose.yml up -d --wait && dotnet build Workflow.sln`

The compose file at `tests/e2e/full-pipeline/docker-compose.yml` starts:
- `neo4j` -- Neo4j 5.26-community on ports 7474/7687
- `qdrant` -- Qdrant latest on ports 6333/6334
- `wiremock` -- WireMock for mock LLM endpoint on port 8089

Environment variables:
- `NEO4J_URI=bolt://localhost:7687`
- `NEO4J_USER=neo4j`
- `NEO4J_PASSWORD` (from compose env)
- `QDRANT_URI=http://localhost:6333`
- `CLEANCODE_LLM_ENDPOINT=http://localhost:8089/v1/chat`

### Scenarios

```gherkin
Feature: Full pipeline end-to-end

  Scenario: Full scan of workflow repository produces valid snapshot
    Given the workflow repository at the current working directory
    And a .cleancode.yaml with default thresholds
    When `ast-cli metrics --repo . --output snapshot.json --format json` runs end-to-end
    Then MetricSnapshot contains at least 20 distinct MetricIds
    And all 6 categories (Coupling, Cohesion, SOLID, Complexity, Architecture, CrossRepo) are represented
    And exit code is 0

  Scenario: Incremental scan detects synthetic regression
    Given a baseline snapshot stored in git notes
    And a synthetic commit adds a class with cyclomatic complexity 25 and 5 responsibilities
    When `ast-cli metrics --repo . --diff HEAD~1 --output delta.sarif --format sarif` runs
    Then the SARIF output contains violations for the new class
    And the violations reference metric IDs "complexity.cyclomatic" and "solid.srp"

  Scenario: Evaluate command with mock LLM produces verdict
    Given a MetricSnapshot at snapshot.json
    And WireMock is configured to return a valid EvaluationVerdict with Outcome=Warn
    When `ast-cli evaluate --snapshot snapshot.json` is invoked
    Then exit code is 2 (warn-only)
    And stdout contains violation details

  Scenario: CI gate blocks on Blocking enforcement
    Given a .cleancode.yaml with complexity.cyclomatic enforcement=Blocking and PassBelow=10
    And a snapshot where a method has cyclomatic complexity 15
    When `ast-cli evaluate --snapshot snap.json` is invoked
    Then exit code is 1
    And the process can be used as a CI quality gate

  Scenario: Cross-repo system scan
    Given 3 repositories indexed in Neo4j with inter-repo dependencies
    When SystemMetricOrchestrator runs with all 3 repo paths
    Then SystemMetricSnapshot contains per-repo snapshots and cross-repo metrics
    And the dashboard JSON output has entries for each repository

  Scenario: Full scan performance within budget
    Given a C# solution with approximately 500 source files
    When `ast-cli metrics --repo .` is invoked
    Then the command completes in less than 5 minutes

  Scenario: Incremental scan performance within budget
    Given a PR with 15 changed files
    When `ast-cli metrics --repo . --diff HEAD~1` is invoked
    Then the command completes in less than 60 seconds
```

```gherkin
Feature: Calibration command

  Scenario: Calibrate suggests thresholds from current state
    Given a repository with existing code of varied quality
    When `ast-cli metrics --calibrate` is invoked
    Then output contains suggested PassBelow values for each metric
    And suggestions are based on P75 percentile of current measurements
    And output format is YAML-compatible for copying into .cleancode.yaml

  Scenario: Calibrate handles empty repository
    Given a repository with no C# source files
    When `ast-cli metrics --calibrate` is invoked
    Then output reports "No metrics computed -- no source files found"
    And exit code is 0
```

```gherkin
Feature: Edge cases and error handling

  Scenario: Repository with no git history
    Given a directory with C# files but no .git folder
    When `ast-cli metrics --repo .` is invoked
    Then metrics are computed (excluding git-dependent ones like change frequency)
    And a warning is logged about missing git history
    And exit code is 0

  Scenario: Empty repository (no source files)
    Given a git repository with no .cs files
    When `ast-cli metrics --repo .` is invoked
    Then an empty MetricSnapshot is produced with zero MetricValues
    And ScoreSummary.Outcome is Pass (nothing to fail)
    And exit code is 0

  Scenario: Single-file repository
    Given a repository with exactly one Program.cs file
    When `ast-cli metrics --repo .` is invoked
    Then metrics are computed at Method and Class scope
    And namespace/project scope metrics are aggregated from the single class

  Scenario: Repository with compilation errors
    Given a repository where dotnet build fails
    When `ast-cli metrics --repo .` is invoked
    Then it degrades gracefully using available HypergraphData from partial parse
    And a warning is logged about incomplete analysis
    And exit code is 0

  Scenario: Invalid --output path
    Given a non-writable output path "/readonly/report.sarif"
    When `ast-cli metrics --repo . --output /readonly/report.sarif` is invoked
    Then an error is reported with descriptive message
    And exit code is 1

  Scenario: Concurrent metric calculation thread safety
    Given a large repository triggering parallel calculator execution
    When MetricOrchestrator.RunAsync processes multiple classes concurrently
    Then no race conditions occur
    And the final MetricSnapshot contains all expected MetricValues without duplicates

  Scenario: Very large class (10000+ lines)
    Given a class with 10000 lines and 200 methods
    When all metric calculators process it
    Then no timeout or out-of-memory errors occur
    And metric values are computed within the performance budget
```

---

## Traceability Matrix

| Story Goal | Covered In Phase(s) | Key Scenarios |
|------------|---------------------|---------------|
| System-level metrics (measurable code qualities) | 2, 3, 6 | All calculator scenarios, orchestrator aggregation, system snapshot assembly |
| Cross-repo measurements | 6, 7 | Dependency depth, blast radius, duplication, knowledge distribution |
| Evaluator agent guarding bad practices | 4, 5, 7 | Evaluator verdict, enforcement levels, CLI exit codes, CI gate blocking |
| Refactoring effort prioritization | 2, 6 | Hotspot ranking, suggested refactoring patterns, composite scoring |
| Decoupled functional areas (metric) | 2, 3 | Coupling calculators (Ca, Ce, I, A, D), cohesion (LCOM4, TCC) |
| SOLID coding principles (metric) | 2, 4 | SRP, OCP, LSP, ISP, DIP calculators; evaluator enforcement |

---

## Non-Functional Requirements Coverage

| NFR | Phase | Verification |
|-----|-------|--------------|
| Full scan < 5 minutes (500 files) | 5, 7 | Performance budget scenarios |
| Incremental scan < 60 seconds | 5, 7 | Diff mode performance scenario |
| Evaluator round-trip < 60 seconds | 4 | Evaluator performance budget scenario |
| Graceful degradation without Neo4j/Qdrant | 6 | Graceful degradation scenarios |
| SARIF 2.1.0 compliance | 5 | Schema validation scenario |
| CLI exit codes 0/1/2 | 5, 7 | Evaluate command scenarios |
| Thread safety under parallel execution | 7 | Concurrent calculation scenario |
