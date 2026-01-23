#!/bin/bash
# Start Neo4j and Qdrant containers for AST graph database

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "Starting Neo4j and Qdrant containers..."
docker compose up -d

echo "Waiting for Neo4j to be ready..."
max_attempts=30
attempt=0

while [ $attempt -lt $max_attempts ]; do
    if curl -s http://localhost:7474 > /dev/null 2>&1; then
        echo "Neo4j is ready!"
        echo ""
        echo "Neo4j Browser: http://localhost:7474"
        echo "Bolt URI: bolt://localhost:7687"
        echo "Username: neo4j"
        echo "Password: password123"
        echo ""
        echo "Qdrant: http://localhost:6333"
        exit 0
    fi
    attempt=$((attempt + 1))
    echo "Waiting for Neo4j... ($attempt/$max_attempts)"
    sleep 2
done

echo "Neo4j failed to start within the timeout period"
docker compose logs neo4j
exit 1
