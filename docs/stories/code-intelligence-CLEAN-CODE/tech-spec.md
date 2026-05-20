# Clean Code Metrics -- Technical Specification

> **Story**: code-intelligence:CLEAN-CODE
> **Points**: 13
> **Status**: Design
> **Sibling docs**: [architecture.md](./architecture.md) | [implementation-plan.md](./implementation-plan.md) | [e2e-scenarios.md](./e2e-scenarios.md)

---

## 1. Problem Statement

Large mono-repos and multi-repo ecosystems at enterprise scale have no automated,
measurable enforcement of clean code standards. Teams adopt SOLID principles and
advocate decoupled functional areas verbally, but there is no system-level metric
infrastructure to:

1. **Quantify** adherence to SOLID (SRP, OCP, LSP, ISP, DIP) and coupling/cohesion
   norms across an entire repository or a fleet of repositories.
2. **Guard** against regressions -- the evaluator agent currently has no numeric
   baseline to compare incoming code changes against.
3. **Prioritize** refactoring effort -- without ranked hotspot data, tech leads
   allocate remediation work by gut feel rather than measured blast radius and
   violation density.

### 1.1 Current State

The existing Code Intelligence subsystem (`src/ast/`) provides:

- AST extraction via `Ast.Core.RepositoryProcessor` producing `HypergraphData`.
- Layer classification via `CodeIntelligence.Analyzers.LayerClassificationAnalyzer`.
- Layer violation detection via `CodeIntelligence.Analyzers.LayerViolationDetector`.
- Architecture metrics (coupling, cohesion, adherence) via
  `CodeIntelligence.Analyzers.ArchitectureMetricsCalculator`.
- Graph storage in Neo4j (`Ast.GraphDb`) and vector embeddings in Qdrant
  (`Ast.VectorDb`).
- CLI commands (`parse`, `enrich`, `embed`, `index`, `query`) via `Ast.Cli`.

**Gap**: These components compute per-class or per-layer metrics but do not:
- Map results onto named SOLID principles with dedicated calculators.
- Produce system-level aggregate scores suitable for cross-repo dashboards.
- Expose a contract that an evaluator agent can consume to gate pull requests.
- Produce SARIF output or refactoring-priority hotspot reports.

### 1.2 Desired End State

After this story ships:

- Running `ast-cli metrics --repo .` produces a `MetricSnapshot` covering 20+
  named metrics across six categories (SOLID, Coupling, Cohesion, Complexity,
  Architecture, CrossRepo).
- Running `ast-cli evaluate --snapshot <path>` invokes the evaluator agent
  contract and returns a pass/warn/fail `EvaluationVerdict`.
- CI pipelines integrate via SARIF output and exit codes (0/1/2).
- A scheduled cross-repo job aggregates per-repo snapshots into a
  `SystemMetricSnapshot` with dashboard JSON output.
- Tech leads run `ast-cli metrics --hotspots --top 20` to get ranked refactoring
  targets with suggested patterns.

---

## 2. In-Scope

| # | Item | Deliverable |
|---|------|-------------|
| S1 | SOLID metric calculators (SRP, OCP, LSP, ISP, DIP) | `CleanCode.Metrics` assembly with `IMetricCalculator` implementations |
| S2 | Coupling/cohesion calculators (Ca, Ce, I, A, D, LCOM4, TCC) | Same assembly, wrapping existing `ArchitectureMetricsCalculator` output |
| S3 | Complexity calculators (cyclomatic, cognitive, WMC) | Same assembly, using Roslyn syntax trees |
| S4 | Architecture calculators (layer adherence, blast radius, debt ratio) | Same assembly, extending `LayerViolationDetector` |
| S5 | Cross-repo metrics (dependency depth, duplication, knowledge distribution) | `ICrossRepoQueryService` extension in `Ast.GraphDb` |
| S6 | Metric orchestration and aggregation | `MetricOrchestrator`, `IMetricAggregator` |
| S7 | Delta calculation for PR gates | `DeltaCalculator` comparing baseline vs. current snapshots |
| S8 | Evaluator agent contract | `CleanCode.Evaluator` assembly with `IEvaluatorContract` and prompt schema |
| S9 | Report renderers (SARIF, JSON dashboard, Markdown) | `CleanCode.Reports` assembly |
| S10 | Hotspot ranking | `HotspotRanker` with weighted composite scoring |
| S11 | CLI integration | New `metrics` and `evaluate` commands in `Ast.Cli` |
| S12 | Configuration schema | `.cleancode.yaml` with policy, thresholds, exclusions |
| S13 | Baseline storage via git notes | `refs/notes/metrics/<branch>` mechanism |

---

## 3. Out of Scope

| # | Item | Reason |
|---|------|--------|
| O1 | Dashboard UI implementation | External concern; we produce JSON payload only (see architecture.md section 10.3) |
| O2 | PowerShell metric support | Deferred to follow-up story; `languages` config accepts only `csharp` initially |
| O3 | Real-time file-watcher mode | IDE integration is a separate story; this delivers batch CLI commands |
| O4 | Custom LLM fine-tuning for evaluator | The evaluator uses structured prompts with standard LLM endpoints |
| O5 | Automatic refactoring execution | Hotspot output suggests patterns; execution is manual or a separate agent |
| O6 | Git hosting provider integrations (GitHub Apps, ADO extensions) | CI invokes the CLI; provider-specific adapters are out of scope |
| O7 | Metrics for non-C# languages (Java, TypeScript, Go) | Future stories may add language-specific calculators |
| O8 | Historical trend storage beyond git notes | Temporal databases or time-series stores are external; JSON export suffices |

---

## 4. Non-Goals

These items are explicitly NOT objectives of this story, even though they may seem
related:

1. **Replacing SonarQube/Roslyn analyzers** -- This system complements, not
   replaces, existing per-file static analysis. The metrics here operate at
   system/architectural level, not at the syntax-warning level.

2. **Enforcing code style** -- Formatting, naming conventions, and whitespace rules
   remain the domain of `.editorconfig` and `dotnet format`. Clean code metrics
   measure structural quality, not cosmetic adherence.

3. **Runtime performance metrics** -- CPU/memory profiling, latency percentiles,
   and throughput measurements are observability concerns, not code quality metrics.

4. **Developer productivity measurement** -- Lines of code, commit frequency, and
   velocity tracking are not part of this system. The metrics measure the code
   artifact, not the humans producing it.

5. **Blocking all PRs on metric failure** -- The evaluator supports `Advisory` and
   `Informational` enforcement levels alongside `Blocking`. Teams choose their own
   strictness via `.cleancode.yaml` policy.

---

## 5. Hard Constraints

### 5.1 Technical Constraints

| # | Constraint | Source |
|---|-----------|--------|
| C1 | .NET 8.0, Windows-only (`win-x64`) | Solution-wide `RuntimeIdentifier` in `Directory.Build.props` |
| C2 | Must integrate with existing `Ast.Core.HypergraphData` as primary input | Existing code intelligence pipeline; no parallel parsing step |
| C3 | Must reuse `ArchitectureMetricsCalculator` output where overlap exists | D7 decision in architecture.md; avoids duplicating coupling/cohesion logic |
| C4 | Evaluator runs as CLI subprocess, not inside Forge agent loop | D2 decision in architecture.md; bounded latency for PR gates (<60s) |
| C5 | SARIF as primary CI output format | Industry standard; integrates with GitHub Advanced Security, ADO, VS Code |
| C6 | Baseline snapshots stored in `refs/notes/metrics/<branch>` | D1 storage decision; no extra branch clutter |
| C7 | All new assemblies must follow existing project conventions | File headers, `this.` qualifier, `using` inside namespace, BDD tests |
| C8 | CLI exit codes: 0 = pass, 1 = fail, 2 = warn-only | CI gate integration contract |
| C9 | Incremental (diff) mode must re-analyze only changed files | PR feedback loop latency requirement |
| C10 | Configuration via `.cleancode.yaml` at repository root | Single source of truth for policy, thresholds, exclusions |

### 5.2 Organizational Constraints

| # | Constraint | Impact |
|---|-----------|--------|
| C11 | Story is 13 points (medium-large) | Scope must be deliverable within a single sprint cycle |
| C12 | Cross-repo metrics require Neo4j and Qdrant availability | CI environments may not have these; cross-repo features degrade gracefully when stores are absent |
| C13 | Evaluator LLM endpoint must be configurable | Teams use different LLM providers; endpoint URL comes from `.cleancode.yaml` or environment variable |

### 5.3 Performance Constraints

| # | Constraint | Target |
|---|-----------|--------|
| P1 | Full repo scan | < 5 minutes for 500-file C# solution |
| P2 | Incremental (diff) scan | < 60 seconds for typical PR (10-50 changed files) |
| P3 | Evaluator round-trip | < 60 seconds including LLM call |
| P4 | Cross-repo system scan | < 30 minutes for 10-repo fleet |

---

## 6. Identified Risks

### 6.1 Technical Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R1 | SOLID metrics are inherently subjective (e.g., "what counts as one responsibility?") | High | Medium | Define calculators with explicit heuristics documented in metric catalog; allow threshold overrides in `.cleancode.yaml`; evaluator provides nuanced judgment for borderline cases |
| R2 | Roslyn workspace loading for complexity metrics may be slow on large solutions | Medium | High | Use incremental mode; cache compiled Roslyn workspaces between runs; fall back to hypergraph-only heuristics if workspace unavailable |
| R3 | Cross-repo metrics require all repos indexed in Neo4j simultaneously | Medium | Medium | Degrade gracefully: skip cross-repo metrics when graph store is unavailable; log warning, do not fail |
| R4 | LLM evaluator responses may be non-deterministic | High | Low | Evaluator contract defines structured JSON schema; prompt includes few-shot examples; non-determinism is acceptable for advisory verdicts |
| R5 | Git notes for baseline storage are not cloned by default | Medium | Medium | Document required `git fetch` flags in CI setup guide; provide `ast-cli baseline --init` command to bootstrap notes |
| R6 | Metric thresholds calibrated poorly may cause alert fatigue | Medium | High | Ship with conservative defaults (warn, not fail); provide `ast-cli metrics --calibrate` to suggest thresholds based on current codebase state |
| R7 | SARIF output schema version drift between GitHub/ADO versions | Low | Medium | Pin to SARIF 2.1.0 (current stable); version field in output; test with both GitHub and ADO parsers |

### 6.2 Scope Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R8 | 13-point story may underestimate cross-repo metric complexity | Medium | High | Cross-repo metrics (S5) are architecturally isolated behind `ICrossRepoQueryService`; can be delivered as a fast-follow if sprint capacity is tight |
| R9 | Evaluator prompt engineering may require multiple iterations to stabilize | Medium | Medium | Ship evaluator contract + default prompt in this story; prompt tuning is iterative and can continue post-merge without interface changes |
| R10 | Dashboard JSON schema may need revision once external consumers integrate | Medium | Low | Schema is versioned (`reporting.dashboardSchemaVersion`); breaking changes increment version; consumers pin to specific version |

### 6.3 Integration Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R11 | Existing `ArchitectureMetricsCalculator` output format may not map cleanly to `MetricValue` | Low | Medium | Adapter pattern: thin wrapper in `CleanCode.Metrics` converts existing `ArchitectureMetrics` to `IReadOnlyList<MetricValue>` |
| R12 | Neo4j Cypher queries for blast radius may time out on dense graphs | Medium | Medium | Add query timeout (30s); cache blast radius results with TTL; index on `:Component(fqn)` |
| R13 | Multiple repos may have conflicting `.cleancode.yaml` policies in cross-repo mode | Low | Low | System-level scan uses union of per-repo policies; conflicts logged as warnings; system-level thresholds override per-repo when explicitly set |

---

## 7. Dependencies

### 7.1 Internal Dependencies (Existing Components)

| Component | Used By | What We Need |
|-----------|---------|--------------|
| `Ast.Core` (`HypergraphData`, `RepositoryProcessor`) | All metric calculators | Parsed hypergraph as primary input |
| `CodeIntelligence.Analyzers` (`ArchitectureMetricsCalculator`, `LayerViolationDetector`, `InheritanceAnalyzer`, `DependencyInjectionAnalyzer`) | Coupling, architecture, SOLID calculators | Existing analysis output as input to metric adapters |
| `Ast.GraphDb` (`IGraphStore`, Neo4j integration) | Cross-repo metrics, blast radius | Cypher query execution |
| `Ast.VectorDb` (Qdrant integration) | Cross-repo duplication detection | Embedding similarity search |
| `Ast.Cli` (Spectre.Console command infrastructure) | `metrics` and `evaluate` commands | Command registration, DI container |

### 7.2 External Dependencies (NuGet Packages)

| Package | Purpose | Version Strategy |
|---------|---------|-----------------|
| `Microsoft.CodeAnalysis.CSharp` (Roslyn) | Cyclomatic/cognitive complexity from syntax trees | Use version already in `Directory.Packages.props` |
| `Sarif.Sdk` | SARIF 2.1.0 report generation | Latest stable |
| `System.Text.Json` | Metric snapshot serialization | Framework-included |

### 7.3 Infrastructure Dependencies

| Dependency | Required For | Graceful Degradation |
|-----------|--------------|---------------------|
| Neo4j instance | Cross-repo queries, blast radius | Skip cross-repo metrics; warn in output |
| Qdrant instance | Cross-repo duplication detection | Skip duplication metric; warn in output |
| LLM endpoint (configurable) | Evaluator agent judgment | Return raw metrics without verdict; exit code based on threshold math only |
| Git (CLI) | Baseline notes, change frequency | Required; fail with clear error if git unavailable |

---

## 8. Assumptions

1. The existing `Ast.Core.RepositoryProcessor` can parse the target repository into
   `HypergraphData` without modification. If the hypergraph lacks metadata needed
   by a calculator (e.g., method-level complexity tags), `Ast.Core` will be
   extended minimally (see architecture.md section 2.2).

2. Teams will adopt `.cleancode.yaml` as the single configuration file. No
   competing configuration mechanisms (environment variables override specific
   fields but do not replace the file).

3. The evaluator LLM endpoint is pre-provisioned and accessible from CI agents.
   Endpoint URL and authentication are configured outside this system (environment
   variable or config file).

4. Neo4j and Qdrant are optional for local developer use. Only cross-repo and
   blast-radius metrics require them. All other metrics work from hypergraph data
   alone.

5. Baseline snapshots accumulate over time in git notes. Pruning/rotation of old
   notes is the repository owner's responsibility (outside this story's scope).

---

## 9. Success Criteria

| # | Criterion | Measurement |
|---|-----------|-------------|
| SC1 | All 20+ metrics in the catalog produce valid numeric output on the `workflow` repository | `ast-cli metrics --repo .` exits 0 with populated `MetricSnapshot` |
| SC2 | Incremental mode correctly identifies regressions introduced by a synthetic "bad" commit | Test fixture with known violations; `DeltaCalculator` flags expected regressions |
| SC3 | Evaluator contract returns structured `EvaluationVerdict` with actionable violation list | Integration test against mock LLM endpoint |
| SC4 | SARIF output validates against SARIF 2.1.0 schema | Schema validation in unit tests |
| SC5 | Hotspot report ranks known-bad classes higher than known-clean classes | Controlled test with injected metrics |
| SC6 | Cross-repo dependency depth computed correctly for a 3-repo test graph | Integration test with test Neo4j data |
| SC7 | CI gate blocks PR when composite score regresses beyond configured threshold | E2E scenario with exit code assertion |

---

## 10. References

- [Augment Code: 12 Code Quality Metrics Every Dev Team Should Track](https://www.augmentcode.com/guides/12-code-quality-metrics-every-dev-team-should-track) -- Primary inspiration for system-level metric categories (cross-repo depth, blast radius, architectural debt ratio, context switch frequency).
- [ast-metrics (GitHub)](https://github.com/Halleck45/ast-metrics/) -- Reference implementation for AST-based metric computation; informs cyclomatic complexity and coupling calculator design.
- Architecture doc (sibling): [architecture.md](./architecture.md) -- Component layout, data model, interface contracts, sequence flows.
- Existing code intelligence architecture: [CODE_INTELLIGENCE_ARCHITECTURE.md](../../CODE_INTELLIGENCE_ARCHITECTURE.md) -- Phases 1-7 completion status, system layer overview.
- Existing requirements: [code_intelligence_requirements.md](../../code_intelligence_requirements.md) -- Multi-strategy code intelligence approach and relationship taxonomy.

---

## 11. Glossary

| Term | Definition |
|------|------------|
| **MetricSnapshot** | Top-level aggregate capturing all metric values for one measurement pass over a repository |
| **EvaluationVerdict** | Pass/warn/fail judgment returned by the evaluator agent after inspecting a snapshot |
| **Baseline** | The MetricSnapshot attached (via git notes) to the target branch's HEAD; used for delta comparison |
| **Hotspot** | A class or namespace ranked high on violation density, change frequency, and blast radius |
| **Blast Radius** | Count of dependent services/components that could break if the target element changes |
| **LCOM4** | Lack of Cohesion of Methods variant 4; counts connected components in the method-field graph |
| **Distance from Main Sequence** | Robert C. Martin's metric: `abs(Abstractness + Instability - 1)` |
| **SARIF** | Static Analysis Results Interchange Format (OASIS standard, version 2.1.0) |
| **Evaluator Agent** | An LLM-backed subprocess that applies judgment on metric snapshots using policy rules |
