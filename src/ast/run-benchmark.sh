#!/bin/bash
# Run the AST Graph Benchmark

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Check if Neo4j is running
if ! curl -s http://localhost:7474 > /dev/null 2>&1; then
    echo "Neo4j is not running. Starting it now..."
    ./start-neo4j.sh
fi

echo "Running AST Graph Benchmark..."
echo ""

# Default to interactive mode with clear flag for first run
if [ "$1" == "benchmark" ]; then
    dotnet run --project Ast.Benchmark/Ast.Benchmark.csproj -c Release -- benchmark "$@"
else
    dotnet run --project Ast.Benchmark/Ast.Benchmark.csproj -c Release -- interactive "$@"
fi
