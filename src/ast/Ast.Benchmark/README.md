# AST Graph Benchmark

Benchmark project for measuring AST extraction and Neo4j ingestion performance. Uses the same real repository configuration as `Ast.GraphDb.IntegrationTests`.

## Prerequisites

- .NET 8.0 SDK
- Docker/Podman for running Neo4j
- A repository to benchmark (default: `/mnt/e/work/hci/update` or `E:\work\hci\update` on Windows)

## Quick Start

### 1. Start Neo4j

```bash
cd src/ast
./start-neo4j.sh
```

Or manually:
```bash
cd src/ast
docker compose up -d
```

### 2. Run Interactive Benchmark

```bash
# Run with default repository
./run-benchmark.sh --clear

# Run with specific repository
./run-benchmark.sh --repo /path/to/repo --clear

# Run with PowerShell extraction
./run-benchmark.sh --repo /path/to/ps-repo --language powershell --clear
```

### 3. Run BenchmarkDotNet Benchmarks

```bash
# Run all benchmarks
./run-benchmark.sh benchmark

# Run specific benchmarks
./run-benchmark.sh benchmark --extraction
./run-benchmark.sh benchmark --ingestion
./run-benchmark.sh benchmark --query
```

## Command Line Options

### Interactive Mode (default)

| Option | Default | Description |
|--------|---------|-------------|
| `-u, --uri` | `bolt://localhost:7687` | Neo4j URI |
| `--user` | `neo4j` | Neo4j username |
| `-p, --password` | `password123` | Neo4j password |
| `-r, --repo` | `E:\work\hci\update` | Repository path to process |
| `-l, --language` | `csharp` | Language to extract (csharp, powershell, or all) |
| `-b, --batch-size` | `500` | Batch size for ingestion |
| `-c, --clear` | `false` | Clear database before benchmark |
| `-s, --stats` | `true` | Show database statistics after benchmark |
| `--sample-queries` | `true` | Run sample queries after benchmark |

### Benchmark Mode

| Option | Description |
|--------|-------------|
| `-e, --extraction` | Run extraction benchmarks |
| `-i, --ingestion` | Run ingestion benchmarks |
| `-q, --query` | Run query benchmarks |

## Benchmark Results

Results from running against the `hci/update` repository (1,027 C# files):

### Extraction Performance

| Metric | Value |
|--------|-------|
| Files Processed | 1,027 |
| Nodes Extracted | 179,076 |
| Edges Extracted | 65,850 |
| Duration | 29.14s |
| **Files/sec** | **35.2** |
| **Nodes/sec** | **6,144.4** |

### Ingestion Performance

| Metric | Value |
|--------|-------|
| Nodes Ingested | 179,076 |
| Edges Ingested | 65,850 |
| Duration | 221.88s |
| **Nodes/sec** | **807.1** |
| **Edges/sec** | **296.8** |

### Total

| Metric | Value |
|--------|-------|
| Total Duration | 254.06s (~4.2 min) |
| End-to-end throughput | ~703 nodes/sec |

### Database Statistics

| Metric | Value |
|--------|-------|
| Total Nodes | 179,076 |
| Total Edges | 109,044 |
| Unique Files | 1,019 |

### Nodes by Type

| Type | Count |
|------|-------|
| expression | 54,586 |
| method_ref | 24,787 |
| type | 21,057 |
| use | 16,147 |
| variable | 15,443 |
| definition | 13,259 |
| parameter | 10,471 |
| method | 4,930 |
| condition | 2,431 |
| field | 2,212 |

### Query Performance

| Query | Latency |
|-------|---------|
| Get all nodes (limit 100) | 26ms |
| Get class nodes (limit 50) | 28ms |
| Get statistics | <50ms |

## Example Output

```
AST Graph Interactive Benchmark

Connected to Neo4j
Database cleared
Processing repository: /mnt/e/work/hci/update

Discovered 1027 C# files, 0 PowerShell files
Extracting AST ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 100%  00:00:29
Extraction complete: 179,076 nodes, 65,850 edges
Ingesting nodes (179,076/179,076) ━━━━━━━━━━━━━━━━ 100%  00:02:45
Ingesting edges (65,850/65,850) ━━━━━━━━━━━━━━━━━━ 100%  00:00:56

─────────────────── Benchmark Results ───────────────────
│ Phase       │ Metric          │ Value           │
├─────────────┼─────────────────┼─────────────────┤
│ Extraction  │ Files Processed │ 1,027           │
│             │ Nodes Extracted │ 179,076         │
│             │ Edges Extracted │ 65,850          │
│             │ Duration        │ 29.14s          │
│             │ Files/sec       │ 35.2            │
│             │ Nodes/sec       │ 6,144.4         │
│ Ingestion   │ Nodes Ingested  │ 179,076         │
│             │ Edges Ingested  │ 65,850          │
│             │ Duration        │ 221.88s         │
│             │ Nodes/sec       │ 807.1           │
│             │ Edges/sec       │ 296.8           │
│ Total       │ Total Duration  │ 254.06s         │
└─────────────┴─────────────────┴─────────────────┘

Done!
```

## Architecture

### Extraction Flow

1. **File Discovery**: Scans repository for `.cs` and `.ps1/.psm1` files
2. **AST Extraction**: Uses `CSharpHyperedgeExtractor` and `PowerShellHyperedgeExtractor`
3. **Progress Tracking**: Real-time progress bars with file-level granularity

### Ingestion Flow

1. **Node Ingestion**: Batch inserts using Cypher UNWIND (batch size: 500)
2. **Edge Ingestion**: Creates relationships between nodes (batch size: 100)
3. **Progress Tracking**: Batch-level progress with throughput metrics

### Benchmarks

- **ExtractionBenchmarks**: Measures AST parsing performance by file size
- **IngestionBenchmarks**: Measures Neo4j write performance by dataset size and batch size
- **QueryBenchmarks**: Measures Neo4j read performance for various query patterns

## Performance Analysis

### Extraction (CPU-bound)
- Roslyn parsing is efficient at ~35 files/sec
- Node generation averages ~174 nodes per file
- Edge generation averages ~64 edges per file

### Ingestion (I/O-bound)
- Node ingestion is the primary bottleneck (~807 nodes/sec)
- Edge ingestion is slower due to MATCH operations (~297 edges/sec)
- Batch size of 500 provides good balance of throughput and memory

### Optimization Opportunities
- Parallel extraction could improve file processing throughput
- Larger batch sizes may improve ingestion (test with IngestionBenchmarks)
- Edge batching by relationship type could reduce Cypher compilation

## Docker Configuration

The Neo4j container is configured with:
- Neo4j 5.26 Community Edition
- APOC plugin enabled
- Authentication: `neo4j / password123`
- Ports: 7474 (HTTP), 7687 (Bolt)
- Health checks enabled

See `docker-compose.yml` for full configuration.
