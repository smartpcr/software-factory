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

## Example Output

```
AST Graph Interactive Benchmark

Connected to Neo4j
Database cleared
Processing repository: /mnt/e/work/hci/update

Discovered 245 C# files, 0 PowerShell files
Extracting AST ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 100%  00:00:00
Extraction complete: 12,456 nodes, 34,567 edges
Ingesting nodes (12,456/12,456) ━━━━━━━━━━━━━━━━ 100%  00:00:00
Ingesting edges (34,567/34,567) ━━━━━━━━━━━━━━━━ 100%  00:00:00

─────────────────── Benchmark Results ───────────────────
│ Phase       │ Metric          │ Value           │
├─────────────┼─────────────────┼─────────────────┤
│ Extraction  │ Files Processed │ 245             │
│             │ Nodes Extracted │ 12,456          │
│             │ Edges Extracted │ 34,567          │
│             │ Duration        │ 3.45s           │
│             │ Files/sec       │ 71.0            │
│             │ Nodes/sec       │ 3,610.4         │
│ Ingestion   │ Nodes Ingested  │ 12,456          │
│             │ Edges Ingested  │ 34,567          │
│             │ Duration        │ 15.23s          │
│             │ Nodes/sec       │ 818.1           │
│             │ Edges/sec       │ 2,270.3         │
│ Total       │ Total Duration  │ 18.68s          │
└─────────────┴─────────────────┴─────────────────┘

Done!
```

## Architecture

### Extraction Flow

1. **File Discovery**: Scans repository for `.cs` and `.ps1/.psm1` files
2. **AST Extraction**: Uses `CSharpHyperedgeExtractor` and `PowerShellHyperedgeExtractor`
3. **Progress Tracking**: Real-time progress bars with file-level granularity

### Ingestion Flow

1. **Node Ingestion**: Batch inserts using Cypher UNWIND
2. **Edge Ingestion**: Creates relationships between nodes
3. **Progress Tracking**: Batch-level progress with throughput metrics

### Benchmarks

- **ExtractionBenchmarks**: Measures AST parsing performance by file size
- **IngestionBenchmarks**: Measures Neo4j write performance by dataset size and batch size
- **QueryBenchmarks**: Measures Neo4j read performance for various query patterns

## Docker Configuration

The Neo4j container is configured with:
- Neo4j 5.26 Community Edition
- APOC plugin enabled
- Authentication: `neo4j / password123`
- Ports: 7474 (HTTP), 7687 (Bolt)
- Health checks enabled

See `docker-compose.yml` for full configuration.
